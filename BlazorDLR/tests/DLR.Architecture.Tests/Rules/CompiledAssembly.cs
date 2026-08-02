using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DLR.Architecture.Tests.Rules;

/// <summary>
/// One compiled DLR assembly, read as metadata rather than loaded.
/// <para>
/// Reading the PE file means a rule can assert on what an assembly <em>refers to</em>
/// without executing any of it, and without a third-party architecture-test package —
/// which would itself have to clear the §14.6.3 licence gate.
/// </para>
/// </summary>
internal sealed class CompiledAssembly
{
	private CompiledAssembly(
		string name,
		string path,
		ImmutableArray<string> assemblyReferences,
		ImmutableArray<string> typeReferences,
		ImmutableArray<string> memberReferences)
	{
		Name = name;
		Path = path;
		AssemblyReferences = assemblyReferences;
		TypeReferences = typeReferences;
		MemberReferences = memberReferences;
	}

	/// <summary>Simple assembly name, for example <c>DLR.Core</c>.</summary>
	public string Name { get; }

	/// <summary>Full path to the <c>.dll</c> that was read.</summary>
	public string Path { get; }

	/// <summary>Simple names of every assembly this one references.</summary>
	public ImmutableArray<string> AssemblyReferences { get; }

	/// <summary>Full names of every type this assembly references from another assembly.</summary>
	public ImmutableArray<string> TypeReferences { get; }

	/// <summary>
	/// Every member this assembly references from another assembly, as
	/// <c>Namespace.Type.Member</c> — so a property read shows up as <c>get_Name</c>.
	/// </summary>
	public ImmutableArray<string> MemberReferences { get; }

	/// <summary>
	/// Every <c>DLR.*</c> assembly sitting beside the running test assembly, this one
	/// included. The architecture test project references the production projects
	/// precisely so that their output lands here and can be inspected.
	/// <para>
	/// The rulebook inspects itself on purpose: <see cref="SourceTree"/> cannot scan the
	/// files that spell out the forbidden identifiers without matching them, so metadata
	/// is what keeps the architecture tests honest about their own rules.
	/// </para>
	/// </summary>
	public static IReadOnlyList<CompiledAssembly> All { get; } = Load();

	/// <summary>The one assembly with the given simple name.</summary>
	public static CompiledAssembly Named(string name)
	{
		CompiledAssembly? found = All.SingleOrDefault(a => a.Name == name);

		if (found is null)
		{
			throw new InvalidOperationException(
				$"No compiled assembly named '{name}' was found beside the test assembly. " +
				$"Found: {string.Join(", ", All.Select(a => a.Name))}. " +
				"A project that is not referenced by DLR.Architecture.Tests is a project no rule can see.");
		}

		return found;
	}

	private static List<CompiledAssembly> Load()
	{
		string directory = AppContext.BaseDirectory;

		return Directory
			.EnumerateFiles(directory, "DLR.*.dll", SearchOption.TopDirectoryOnly)
			.Select(Read)
			.OrderBy(a => a.Name, StringComparer.Ordinal)
			.ToList();
	}

	private static CompiledAssembly Read(string path)
	{
		using FileStream stream = File.OpenRead(path);
		using PEReader pe = new(stream);
		MetadataReader metadata = pe.GetMetadataReader();

		ImmutableArray<string> assemblyReferences = metadata.AssemblyReferences
			.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
			.Distinct(StringComparer.Ordinal)
			.ToImmutableArray();

		ImmutableArray<string> typeReferences = metadata.TypeReferences
			.Select(handle => FullNameOf(metadata, handle))
			.Distinct(StringComparer.Ordinal)
			.ToImmutableArray();

		ImmutableArray<string> memberReferences = metadata.MemberReferences
			.Select(handle => metadata.GetMemberReference(handle))
			.Where(member => member.Parent.Kind == HandleKind.TypeReference)
			.Select(member =>
				$"{FullNameOf(metadata, (TypeReferenceHandle)member.Parent)}.{metadata.GetString(member.Name)}")
			.Distinct(StringComparer.Ordinal)
			.ToImmutableArray();

		return new CompiledAssembly(
			metadata.GetString(metadata.GetAssemblyDefinition().Name),
			path,
			assemblyReferences,
			typeReferences,
			memberReferences);
	}

	private static string FullNameOf(MetadataReader metadata, TypeReferenceHandle handle)
	{
		TypeReference type = metadata.GetTypeReference(handle);
		string name = metadata.GetString(type.Name);

		// A nested type's resolution scope is its declaring type, not a namespace.
		if (type.ResolutionScope.Kind == HandleKind.TypeReference)
		{
			return $"{FullNameOf(metadata, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
		}

		string @namespace = metadata.GetString(type.Namespace);

		return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
	}
}
