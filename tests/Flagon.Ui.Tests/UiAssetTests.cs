using System.Net;
using Flagon.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace Flagon.Ui.Tests;

public class UiAssetTests
{
    [Fact]
    public async Task The_console_page_is_served_at_the_configured_path()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Endpoints = app => app.MapFlagonUi("/flags"));

        var response = await host.CreateClient().GetAsync("/flags/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Flagon</title>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
