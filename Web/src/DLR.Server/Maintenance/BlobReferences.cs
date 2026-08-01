using DLR.Server.Data.Photos;
using DLR.Server.Data.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DLR.Server.Maintenance;

/// <summary>One column that holds a blob reference, named as the database names it.</summary>
/// <param name="Table">The table.</param>
/// <param name="Column">The column.</param>
public readonly record struct BlobColumn(string Table, string Column);

/// <summary>
/// Everywhere a blob reference is stored — the subtrahend of the orphan sweep (§7.11, §16.6).
/// <para>
/// <strong>Getting this list wrong is not a missed tidy-up.</strong> A blob column the sweep does
/// not know about is a column whose every value looks unreferenced, so the next run deletes every
/// file it names. That is the one failure in this project that destroys user data quietly and
/// completely, which is why the list is declared here, resolved through the EF model so a rename
/// cannot silently drop an entry, and guarded by a test that scans the model for anything
/// blob-shaped this list has missed.
/// </para>
/// </summary>
public static class BlobReferences
{
	/// <summary>
	/// The four, by CLR type and property. Declared rather than discovered by naming convention:
	/// a convention would quietly cover a new column, and quietly covering a new column is how the
	/// author of that column never thinks about whether the sweep should own its bytes.
	/// </summary>
	private static readonly (Type Entity, string Property)[] Declared =
	[
		(typeof(Track), nameof(Track.BlobRef)),
		(typeof(TrackRevision), nameof(TrackRevision.BlobRef)),
		(typeof(Photo), nameof(Photo.BlobRef)),
		(typeof(Photo), nameof(Photo.ThumbBlobRef)),
	];

	/// <summary>Resolves the declared columns against a model.</summary>
	/// <param name="model">The context's model.</param>
	/// <exception cref="InvalidOperationException">
	/// A declared entity or property is not in the model any more. Thrown rather than skipped: a
	/// renamed column that silently dropped out of the list would turn every blob it holds into a
	/// deletion candidate on the next run.
	/// </exception>
	public static IReadOnlyList<BlobColumn> InModel(IModel model)
	{
		List<BlobColumn> columns = [];

		foreach ((Type entity, string property) in Declared)
		{
			IEntityType type = model.FindEntityType(entity)
				?? throw new InvalidOperationException(
					$"{entity.Name} is not in the model, so the orphan blob sweep cannot tell " +
					"which of its blobs are still referenced.");

			IProperty column = type.FindProperty(property)
				?? throw new InvalidOperationException(
					$"{entity.Name}.{property} is not in the model. If it was renamed, rename it " +
					"here too — every blob it holds is a deletion candidate until you do.");

			columns.Add(new BlobColumn(type.GetTableName()!, column.GetColumnName()));
		}

		return columns;
	}
}
