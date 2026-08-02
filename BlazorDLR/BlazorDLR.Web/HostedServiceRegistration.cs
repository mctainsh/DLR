using Microsoft.Extensions.Hosting;

namespace DLR.Server;

/// <summary>Registration helpers for services that are both a singleton and a hosted service.</summary>
public static class HostedServiceRegistration
{
	/// <summary>
	/// Registers <typeparamref name="T"/> as a singleton <em>and</em> hosts it. Endpoints resolve
	/// the same object the timer drives — a second registration would leave two copies reading
	/// the same settings, which is precisely what the container validation exists to catch.
	/// </summary>
	public static IServiceCollection AddSingletonHostedService<T>(this IServiceCollection services)
		where T : class, IHostedService
	{
		services.AddSingleton<T>();
		services.AddHostedService(provider => provider.GetRequiredService<T>());
		return services;
	}
}
