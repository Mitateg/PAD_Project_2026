using Cassandra;
using PadLogger = Pad.Common.Logger;

// ASP.NET are si el un ISession (sesiunea HTTP), deci numim explicit pe cel din Cassandra.
using ISession = Cassandra.ISession;

namespace Pad.Warehouse.Data;

/// <summary>
/// Conectarea la Cassandra si crearea schemei la pornire.
/// Cassandra are nevoie de 30-60 s sa porneasca in Docker, deci reincercam pana raspunde.
/// </summary>
public static class CassandraSession
{
    /// <summary>
    /// Cat timp pastram evidenta schimbarilor (employee_changes). Dupa acest interval Cassandra sterge singura randurile,
    /// deci tabelul nu creste nelimitat. Un pull-request cu o data mai veche primeste doar ce s-a pastrat.
    /// </summary>
    public const int ChangeRetentionDays = 7;

    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(5);

    private static readonly string[] Schema =
    {
        "CREATE KEYSPACE IF NOT EXISTS pad WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}",

        "CREATE TABLE IF NOT EXISTS pad.employees (" +
        "id int PRIMARY KEY, name text, position text, department text, updated_at timestamp)",

        // Evidenta schimbarilor, pentru /update/employees/. Partitia este ziua (bucket = '2026-11-14'), nu una singura
        // pentru tot timpul, si fiecare rand expira dupa ChangeRetentionDays: tabelul are marime marginita.
        "CREATE TABLE IF NOT EXISTS pad.employee_changes (" +
        "bucket text, changed_at timeuuid, employee_id int, PRIMARY KEY (bucket, changed_at)) " +
        "WITH CLUSTERING ORDER BY (changed_at DESC) " +
        $"AND default_time_to_live = {ChangeRetentionDays * 24 * 3600}",

        // Marcaj "acest id a fost creat". INSERT ... IF NOT EXISTS aici decide atomic cine primeste 201, chiar daca
        // doua PUT-uri pentru acelasi id nou ajung simultan (pe acelasi nod sau pe noduri diferite).
        // Nu putem pune IF NOT EXISTS direct pe tabelul employees: Cassandra nu accepta USING TIMESTAMP pe
        // instructiuni conditionale, iar last-write-wins depinde de USING TIMESTAMP.
        "CREATE TABLE IF NOT EXISTS pad.employee_ids (id int PRIMARY KEY)",
    };

    /// <summary>
    /// Un singur Cluster si o singura ISession pentru tot procesul (se inregistreaza ca singleton).
    /// Dupa MaxAttempts incercari esuate arunca exceptia: procesul se opreste, iar Docker il poate reporni.
    /// </summary>
    public static async Task<ISession> ConnectAsync(string host, PadLogger log)
    {
        for (int attempt = 1; ; attempt++)
        {
            ICluster? cluster = null;
            try
            {
                cluster = Cluster.Builder()
                    .AddContactPoint(host)
                    .WithQueryTimeout(5000)
                    .Build();

                ISession session = await cluster.ConnectAsync();

                foreach (string cql in Schema)
                    await session.ExecuteAsync(new SimpleStatement(cql));

                log.Info("cassandra_ready", result: host);
                return session;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                log.Warn("cassandra_not_ready", result: $"{host} attempt {attempt}/{MaxAttempts}: {ex.Message}");

                try { cluster?.Shutdown(1000); } catch { /* nu e critic */ }
                await Task.Delay(RetryPause);
            }
        }
    }
}
