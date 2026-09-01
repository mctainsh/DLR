using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Identity;

/// <summary>
/// The two username rules Identity has no setting for: length, and the reserved list (§7.2).
/// <para>
/// Registered alongside Identity's own <see cref="UserValidator{TUser}"/> rather than
/// replacing it - that one owns the allowed-character check and the uniqueness lookup, and
/// re-implementing either in order to add a length bound would be trading a bug for a
/// feature.
/// </para>
/// </summary>
public sealed class UserNameValidator : IUserValidator<AppUser>
{
	/// <summary>Error code for a username outside the length bounds.</summary>
	public const string InvalidLengthCode = "UserNameLength";

	/// <summary>Error code for a username on the reserved list.</summary>
	public const string ReservedCode = "UserNameReserved";

	/// <inheritdoc />
	public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user)
	{
		string userName = user.UserName ?? string.Empty;
		List<IdentityError> errors = [];

		if (userName.Length is < UserNameRules.MinimumLength or > UserNameRules.MaximumLength)
		{
			errors.Add(new IdentityError
			{
				Code = InvalidLengthCode,
				Description =
					$"A username is between {UserNameRules.MinimumLength} and " +
					$"{UserNameRules.MaximumLength} characters.",
			});
		}

		if (ReservedUserNames.IsReserved(userName))
		{
			errors.Add(new IdentityError
			{
				Code = ReservedCode,
				Description = $"'{userName}' is reserved and cannot be registered.",
			});
		}

		return Task.FromResult(errors.Count == 0
			? IdentityResult.Success
			: IdentityResult.Failed([.. errors]));
	}
}
