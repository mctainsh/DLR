using BlazorDLR.Shared.Services;
using BlazorDLR.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace BlazorDLR.Web.Client;

internal class Program
{
	static async Task Main(string[] args)
	{
		var builder = WebAssemblyHostBuilder.CreateDefault(args);

		// Add device-specific services used by the BlazorDLR.Shared project
		builder.Services.AddSingleton<IFormFactor, FormFactor>();

		await builder.Build().RunAsync();
	}
}
