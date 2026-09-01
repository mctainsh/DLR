using BlazorDLR.Shared.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// bUnit v2 pre-registers a placeholder <c>IAuthorizationService</c> that throws on
/// any <c>AuthorizeAsync</c> call, and does not wire the framework's default
/// authorization pipeline. Every component that carries
/// <c>@attribute [Authorize]</c> or hosts an <c>AuthorizeView</c> needs the real
/// pipeline. The extensions below make that a one-line wire-up and cascade the
/// <see cref="AuthenticationState"/> task the same way <c>App.razor</c> would in
/// production.
/// </summary>
internal static class AuthorizationTestHelpers
{
	/// <summary>
	/// Replace bUnit's placeholder authorization service with the framework's default
	/// pipeline and register the empty policy provider - enough for
	/// <c>@attribute [Authorize]</c> without a specific policy name.
	/// </summary>
	public static void AddRealAuthorizationPipeline(this IServiceCollection services)
	{
		Microsoft.Extensions.DependencyInjection.ServiceDescriptor? placeholder = services
			.FirstOrDefault(s => s.ServiceType == typeof(Microsoft.AspNetCore.Authorization.IAuthorizationService));
		if (placeholder is not null) services.Remove(placeholder);

		services.AddAuthorizationCore();
		services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationService,
			Microsoft.AspNetCore.Authorization.DefaultAuthorizationService>();
		services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandlerContextFactory,
			Microsoft.AspNetCore.Authorization.DefaultAuthorizationHandlerContextFactory>();
		services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationEvaluator,
			Microsoft.AspNetCore.Authorization.DefaultAuthorizationEvaluator>();
		services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandlerProvider,
			Microsoft.AspNetCore.Authorization.DefaultAuthorizationHandlerProvider>();
		services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
			Microsoft.AspNetCore.Authorization.DefaultAuthorizationPolicyProvider>();
		services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
			Microsoft.AspNetCore.Authorization.Infrastructure.PassThroughAuthorizationHandler>();
	}

	/// <summary>
	/// Cascade the auth state on the render tree of a <see cref="BunitContext"/> - the
	/// equivalent of wrapping the app in <c>&lt;CascadingAuthenticationState&gt;</c>.
	/// Call this once per test-class ctor after registering an <see cref="AuthState"/>.
	/// </summary>
	public static void CascadeAuthenticationState(this BunitContext context, AuthState auth)
	{
		Task<AuthenticationState> state = auth.GetAuthenticationStateAsync();
		context.RenderTree.Add<CascadingValue<Task<AuthenticationState>>>(parameters => parameters
			.Add(cv => cv.Value, state));
	}
}
