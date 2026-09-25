using System.Reflection;
using System.Text.Json;
using NetRoute.Core.Config;
using NetRoute.Ipc;

namespace NetRoute.Service;

public interface IUpdateSource
{
    UpdateDto? Available { get; }
}

public sealed class NoUpdates : IUpdateSource
{
    public UpdateDto? Available => null;
}

/// <summary>
/// Looks for a newer NetRoute, once a day, and only when the user has given it somewhere to look.
///
/// <para>No feed URL is configured by default, so a fresh install never calls anywhere. When one
/// is set, the check is a single GET of a small JSON document
/// (<c>{ "version": "1.1.0", "url": "...", "notes": "..." }</c>), and finding something new only
/// ever shows a message with a link: NetRoute does not download or install anything behind the
/// user's back.</para>
/// </summary>
public sealed class UpdateChecker(ILogger<UpdateChecker> logger) : BackgroundService, IUpdateSource
{
    private static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public UpdateDto? Available { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(new ConfigStore().Load(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Update check failed.");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task CheckAsync(NetRouteConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.UpdateFeedUrl))
        {
            Available = null;
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"NetRoute/{Current.ToString(3)}");
        using var document = JsonDocument.Parse(await http.GetStringAsync(config.UpdateFeedUrl, ct));
        var root = document.RootElement;

        if (!root.TryGetProperty("version", out var versionText) || !Version.TryParse(versionText.GetString(), out var version)
            || !root.TryGetProperty("url", out var url))
        {
            logger.LogInformation("The update feed didn't contain a version and a url.");
            return;
        }

        Available = version > Current
            ? new UpdateDto(version.ToString(3), url.GetString() ?? "",
                root.TryGetProperty("notes", out var notes) ? notes.GetString() : null)
            : null;
    }
}
