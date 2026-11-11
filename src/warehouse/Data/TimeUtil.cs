namespace Pad.Warehouse.Data;

internal static class TimeUtil
{
    /// <summary>
    /// Aduce un DateTime la UTC. Un DateTime fara "kind" (ex. din JSON fara 'Z') il tratam ca fiind
    /// deja UTC, nu ca ora locala, ca rezultatul sa nu depinda de fusul orar al masinii.
    /// </summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Microsecunde de la 1970-01-01 UTC: unitatea timestamp-ului de scriere din Cassandra.</summary>
    public static long ToMicros(DateTime utc) => (AsUtc(utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;
}
