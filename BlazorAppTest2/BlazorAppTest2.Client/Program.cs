using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace BlazorAppTest2.Client;

internal class Program
{
    static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);

        builder.Services.AddScoped<INavigationService, NavigationService>();

        await builder.Build().RunAsync();
    }
}
