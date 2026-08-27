using DLR.TestSupport.Database;

// One PostgreSQL container for the whole assembly, and one throwaway database per test (§10.4).
//
// An assembly fixture rather than a collection fixture, which is what this was. xUnit's collection
// is two things at once — the unit a fixture is shared across, and the unit that runs in parallel —
// and only the first of those was ever wanted here. Joining one collection to share a container
// therefore also put forty test classes in a single-file queue: the suite ran one test at a time on
// a machine with thirty-two cores, and nothing said so. Assembly fixtures share without that
// second meaning, so each class is its own collection again and the classes run together.
//
// Sharing stays safe because it never included the data: every test still builds its own database
// and its own server, so parallel classes cannot see each other's rows.
[assembly: AssemblyFixture(typeof(PostgresFixture))]
