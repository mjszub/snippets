open System
open System.Threading
open Xunit

open System
open System.Threading

/// Typy zdarzeń
/// OnLogon i OnLogout to eventy, które zewnętrznie wywołuje handler
/// Handler ma sygnaturę ConnectionEvent -> unit

type ConnectionEvent = OnLogon | OnLogout

type Config = {
    DisconnectAfter: TimeSpan
    ReconnectAfter: TimeSpan
}

type State =
    | Connected
    | PendingDisconnect of since: DateTime
    | Disconnected
    | PendingReconnect of since: DateTime

/// Wiadomości dla MailboxProcessor
type Msg =
    | Event of ConnectionEvent
    | Tick of DateTime
    | Stop of AsyncReplyChannel<unit>

/// Monitor aktywności i diagnostyka
module Diagnostics =
    /// Loguje aktualny stan pamięci
    let logMemory () =
        let proc = System.Diagnostics.Process.GetCurrentProcess()
        let memMb = proc.PrivateMemorySize64 / (int64 <| 1024 * 1024)
        printfn "%O [Diagnostics] Memory usage: %d MB" DateTime.UtcNow memMb

    /// Loguje aktywność monitora
    let logActivity msg =
        printfn "%O [Activity] %s" DateTime.UtcNow msg

/// Funkcja bezpiecznie wywołująca handler, łapiąca wyjątki
let safeEmit (handler: ConnectionEvent -> unit) (ev: ConnectionEvent) =
    try
        handler ev
    with ex ->
        printfn "%O [Error] Handler exception on %A: %s" DateTime.UtcNow ev ex.Message

/// Przetwarzanie eventów bez timeoutu (bez opóźnienia)
let inline immediatePath (handler: ConnectionEvent -> unit) (ev: ConnectionEvent) =
    Diagnostics.logActivity (sprintf "Immediate emit: %A" ev)
    safeEmit handler ev

/// Przetwarzanie eventów z timeoutem
let handleEvent (cfg: Config) (now: DateTime) (state: State) (ev: ConnectionEvent) (handler: ConnectionEvent -> unit) =
    // Ścieżka natychmiastowa
    if cfg.DisconnectAfter = TimeSpan.Zero && cfg.ReconnectAfter = TimeSpan.Zero then
        immediatePath handler ev
        state
    else
        match state, ev with
        | Connected, OnLogout -> PendingDisconnect now
        | PendingDisconnect t0, OnLogout -> PendingDisconnect t0
        | PendingDisconnect t0, OnLogon when now - t0 < cfg.DisconnectAfter -> Connected
        | PendingDisconnect t0, OnLogon -> PendingReconnect now
        | Disconnected, OnLogon -> PendingReconnect now
        | PendingReconnect t1, OnLogout -> Disconnected
        | PendingReconnect t1, OnLogon when now - t1 >= cfg.ReconnectAfter -> Connected
        | _ -> state

let checkTimeouts (cfg: Config) (now: DateTime) (state: State) (handler: ConnectionEvent -> unit) =
    if cfg.DisconnectAfter = TimeSpan.Zero && cfg.ReconnectAfter = TimeSpan.Zero then
        state
    else
        match state with
        | PendingDisconnect t0 when now - t0 >= cfg.DisconnectAfter ->
            Diagnostics.logActivity "Timeout: emit OnLogout"
            safeEmit handler OnLogout
            Disconnected
        | PendingReconnect t1 when now - t1 >= cfg.ReconnectAfter ->
            Diagnostics.logActivity "Timeout: emit OnLogon"
            safeEmit handler OnLogon
            Connected
        | _ -> state

/// Tworzy i uruchamia monitor
let createMonitor (cfg: Config) (handler: ConnectionEvent -> unit) =
    Diagnostics.logActivity "Monitor starting..."
    Diagnostics.logMemory ()
    let mailbox =
        MailboxProcessor.Start(fun inbox ->
            let rec loop state = async {
                let! msg = inbox.Receive()
                let now = DateTime.UtcNow
                match msg with
                | Event ev ->
                    Diagnostics.logActivity (sprintf "Received event: %A" ev)
                    let newState = handleEvent cfg now state ev handler
                    Diagnostics.logMemory ()
                    return! loop newState
                | Tick tickTime ->
                    let newState = checkTimeouts cfg tickTime state handler
                    Diagnostics.logMemory ()
                    return! loop newState
                | Stop reply ->
                    Diagnostics.logActivity "Monitor stopping."
                    reply.Reply ()
            }
            loop Connected
        )
    mailbox

/// Uruchamia pętlę ticków co 1 sekundę oraz watchdoga
let startTicker (mb: MailboxProcessor<Msg>) =
    let cts = new CancellationTokenSource()
    // Ticker
    Async.Start(
        async {
            while not cts.IsCancellationRequested do
                do! Async.Sleep 1000
                mb.Post (Tick DateTime.UtcNow)
        }, cts.Token)

    // Watchdog: co 60s sprawdza pamięć i loguje
    Async.Start(
        async {
            while not cts.IsCancellationRequested do
                do! Async.Sleep (60 * 1000)
                Diagnostics.logActivity "Watchdog ping"
                Diagnostics.logMemory ()
        }, cts.Token)
    cts

/// Przykład użycia
let config = { DisconnectAfter = TimeSpan.FromSeconds 5.0; ReconnectAfter = TimeSpan.FromSeconds 10.0 }

let handler ev =
    match ev with
    | OnLogout -> printfn "🔴 Emitted LOGOUT"
    | OnLogon  -> printfn "🟢 Emitted LOGON"

let monitor = createMonitor config handler
let tickerCts = startTicker monitor

// Symulacja zdarzeń:
monitor.Post (Event OnLogout)
Thread.Sleep 6000  // po 5s wypisze LOGOUT

// Poprawne zatrzymanie
async {
    let! _ = monitor.PostAndAsyncReply(fun rc -> Stop rc)
    tickerCts.Cancel()
} |> Async.RunSynchronously



[<Fact>]
let ``Reconnect before timeout doesn't emit logout`` () =
    let now = DateTime.UtcNow
    let emitted = ResizeArray()
    let config = { DisconnectAfter = TimeSpan.FromSeconds 5.0; ReconnectAfter = TimeSpan.FromSeconds 10.0 }
    let state1 = handleEvent config now Connected OnLogout emitted.Add
    let state2 = handleEvent config (now.AddSeconds 1.0) state1 OnLogon emitted.Add
    Assert.Equal(0, emitted.Count)
    Assert.Equal(Connected, state2)

[<Fact>]
let ``Timeout emits logout after disconnect`` () =
    let now = DateTime.UtcNow
    let emitted = ResizeArray()
    let config = { DisconnectAfter = TimeSpan.FromSeconds 5.0; ReconnectAfter = TimeSpan.FromSeconds 10.0 }
    let state1 = handleEvent config now Connected OnLogout emitted.Add
    let state2 = checkTimeouts config (now.AddSeconds 6.0) state1 emitted.Add
    Assert.Equal(1, emitted.Count)
    Assert.Equal(OnLogout, emitted.[0])
    Assert.Equal(Disconnected, state2)

[<Fact>]
let ``Stable reconnect after timeout emits logon`` () =
    let now = DateTime.UtcNow
    let emitted = ResizeArray()
    let config = { DisconnectAfter = TimeSpan.FromSeconds 5.0; ReconnectAfter = TimeSpan.FromSeconds 10.0 }
    let state1 = handleEvent config now Connected OnLogout emitted.Add
    let state2 = checkTimeouts config (now.AddSeconds 6.0) state1 emitted.Add
    let state3 = handleEvent config (now.AddSeconds 7.0) state2 OnLogon emitted.Add
    let state4 = checkTimeouts config (now.AddSeconds 18.0) state3 emitted.Add
    Assert.Equal(2, emitted.Count)
    Assert.Equal(OnLogout, emitted.[0])
    Assert.Equal(OnLogon, emitted.[1])
    Assert.Equal(Connected, state4)

[<Fact>]
let ``Immediate mode emits directly`` () =
    let now = DateTime.UtcNow
    let emitted = ResizeArray()
    let config = { DisconnectAfter = TimeSpan.FromSeconds 5.0; ReconnectAfter = TimeSpan.FromSeconds 10.0 }
    let config = { DisconnectAfter = TimeSpan.Zero; ReconnectAfter = TimeSpan.Zero }
    let state1 = handleEvent config now Connected OnLogout emitted.Add
    Assert.Equal(1, emitted.Count)
    Assert.Equal(OnLogout, emitted.[0])
    Assert.Equal(Connected, state1) // bo w immediate trybie stan się nie zmienia
