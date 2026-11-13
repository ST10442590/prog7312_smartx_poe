using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The gateway address comes from wwwroot/appsettings.json, which Blazor
// WebAssembly loads into configuration at startup. Hard-coding it here would
// mean rebuilding the client to point it at a different deployment, and the
// value would be invisible to anyone reading the configuration rather than
// the source. Falling back to the client's own origin keeps the app booting
// if the setting is missing, rather than throwing before the UI renders.
var apiBaseAddress = builder.Configuration["ApiBaseAddress"];
if (string.IsNullOrWhiteSpace(apiBaseAddress))
{
    apiBaseAddress = builder.HostEnvironment.BaseAddress;
}

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(apiBaseAddress)
});

builder.Services.AddScoped<SmartXApiClient>();

await builder.Build().RunAsync();