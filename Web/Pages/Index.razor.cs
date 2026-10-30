using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Xna.Platform;
using TheTimelineIs.Core;

namespace TheTimelineIs.Web.Pages;

/// <summary>
/// The page the game runs in. It downloads all the content first, with a
/// progress bar, then starts the game. The browser calls TickDotNet once per
/// frame (requestAnimationFrame in index.html), and each call is one game frame.
/// </summary>
public partial class Index
{
    private TimelineGame? _game;
    private bool _loaded;
    private int _percent;
    private string _status = "";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        var preload = new ContentPreload();
        await preload.LoadAsync(Http, (done, total, bytes) =>
        {
            int percent = total == 0 ? 100 : done * 100 / total;
            if (percent == _percent) return;
            _percent = percent;
            _status = $"{bytes / 1048576.0:0.0} MB";
            StateHasChanged();
        });
        TitleContainerFactory.RegisterTitleContainerFactory(preload);

        _loaded = true;
        StateHasChanged();
        await JsRuntime.InvokeAsync<object>("initRenderJS", DotNetObjectReference.Create(this));
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
