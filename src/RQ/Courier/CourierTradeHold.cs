using System.Text.Json;
using Dalamud.Plugin;

namespace RQ.Courier;

internal interface ICourierHoldPort
{
    Task<Result<IDisposable>> BeginHold(Guid run, Deadline deadline);
    void CheckHold();
    /// <summary>Ahead of a trade: re-acquires, once, a hold that lapsed between steps; refuses when it cannot.</summary>
    Task<Result<bool>> RegainHoldAsync(Deadline deadline) { CheckHold(); return Task.FromResult(Result<bool>.Ok(true)); }
}

// Acquire on the framework thread where MMF can inspect its live runner/window.
// Renew is bookkeeping-only and continues across travel, relogs and peer waits.
internal sealed class CourierTradeHold(string holder, Func<bool> loaded, Func<bool> available, Func<string> acquire,
    Func<bool> renew, Action release, Action<string> warn) : IDisposable
{
    private const string Prefix = "MarketMafioso.v1.TradeHold.";
    private const int Seconds = 30;
    internal CourierTradeHold(IDalamudPluginInterface pi,string holder,Action<string> warn) : this(holder,
        ()=>pi.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName=="MarketMafioso"),
        ()=>pi.GetIpcSubscriber<string,int,string>(Prefix+"Acquire").HasFunction &&
            pi.GetIpcSubscriber<string,int,bool>(Prefix+"Renew").HasFunction && pi.GetIpcSubscriber<string,bool>(Prefix+"Release").HasFunction,
        ()=>pi.GetIpcSubscriber<string,int,string>(Prefix+"Acquire").InvokeFunc(holder,Seconds),
        ()=>pi.GetIpcSubscriber<string,int,bool>(Prefix+"Renew").InvokeFunc(holder,Seconds),
        ()=>pi.GetIpcSubscriber<string,bool>(Prefix+"Release").InvokeFunc(holder),warn) {}
    internal Result<IDisposable> Begin()
    {
        try { Check(); return Result<IDisposable>.Ok(this); }
        catch(CourierFault e) { Dispose(); return Result<IDisposable>.Fail(e.Code,e.Message); }
    }
    private readonly object gate = new();
    private Timer? timer;
    private bool held, disposed, regaining;
    private CourierFault? loss;
    private long expires;


    /// <summary>
    /// Throws when the hold is lost. <paramref name="between"/> says no trade is open or in flight here: a hold lost
    /// then (MarketMafioso lets a hold lapse) is asked for once more, on the framework thread; off it, the loss waits
    /// for that framework check ahead of the next trade. A loss mid-trade, or a refused re-acquire, still throws.
    /// </summary>
    internal void Check(bool framework = true, bool between = false)
    {
        lock (gate)
        {
            if (disposed) throw new CourierFault(CourierOutcome.Failed,"TradeHoldLost","Courier hold was released.");
            if (held && loss == null && (Environment.TickCount64 >= expires || (framework && !loaded())))
                loss = new(CourierOutcome.Failed,"TradeHoldLost","MarketMafioso hold expired or provider unloaded.");
            if (loss != null)
            {
                if (!between || regaining || loss.Code != "TradeHoldLost") throw loss;
                if (!framework) return;
                warn($"TradeHoldReacquire: {holder}: {loss.Message}; no trade is open, asking MarketMafioso again.");
                regaining = true; loss = null; held = false; timer?.Dispose(); timer = null;
            }
            if (held) return;
            if (!framework || !loaded()) return;
            try
            {
                if (!available())
                    throw new InvalidOperationException("Missing MarketMafioso v1 TradeHold gates.");
                var start = Environment.TickCount64;
                using var json = JsonDocument.Parse(acquire());
                if (!json.RootElement.GetProperty("Acknowledged").GetBoolean())
                    throw new CourierFault(CourierOutcome.Failed,"TradeHoldRefused",json.RootElement.GetProperty("Reason").GetString() ?? "Refused");
                held = true; regaining = false;
                expires = start + Seconds*1000;
                timer = new Timer(_ => Renew(),null,3000,3000);
            }
            // A refused re-acquire is the hold's loss for good.
            catch (CourierFault e) { if (regaining) loss = e; throw; }
            catch (Exception e)
            {
                var fault = new CourierFault(CourierOutcome.Failed,"TradeHoldUnavailable",e.Message);
                if (regaining) loss = fault;
                throw fault;
            }
        }
    }
    private void Renew()
    {
        lock (gate)
        {
            if (disposed || loss != null) return;
            var start = Environment.TickCount64;
            try
            {
                if (start >= expires || !renew())
                    throw new InvalidOperationException("MarketMafioso did not renew "+holder);
                expires = start + Seconds*1000;
            }
            catch (Exception e) { loss = new(CourierOutcome.Failed,"TradeHoldLost",e.Message); warn(e.Message); }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer?.Dispose();
            try { if (held) release(); }
            catch (Exception e) { warn("TradeHoldRelease: "+e.Message); }
        }
    }
}
