using Microsoft.JSInterop;
using TheTimelineIs.Core;

namespace TheTimelineIs.Web.Pages;

/// <summary>
/// The page the game runs in. The browser calls TickDotNet once per frame
/// (requestAnimationFrame in index.html), and each call is one game frame.
/// </summary>
public partial class Index
{
    private TimelineGame? _game;

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        if (firstRender)
            JsRuntime.InvokeAsync<object>("initRenderJS", DotNetObjectReference.Create(this));
    }

    [JSInvokable]
    public void TickDotNet()
    {
        if (_game == null)
        {
            _game = new TimelineGame(new WebPlatform());
            _game.Run();
        }
        _game.Tick();
    }
}
