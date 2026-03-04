using Microsoft.Data.Sqlite;

namespace SpotPriceDashboard.Data;

/// <summary>
/// Deep module (A Philosophy of Software Design): hides all SQLite complexity
/// behind a small, obvious interface. Callers never see SQL or connections.
/// </summary>
public sealed class PriceDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public PriceDatabase(string dbPath = "spotprices.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA auto_vacuum = INCREMENTAL;

            CREATE TABLE IF NOT EXISTS spot_prices (
                vm_size       TEXT NOT NULL,
                region        TEXT NOT NULL,
                spot_price    REAL NOT NULL,
                ondemand_price REAL NOT NULL,
                vcpus         INTEGER NOT NULL,
                memory_gb     REAL NOT NULL,
                vm_family     TEXT NOT NULL,
                friendly_cat  TEXT NOT NULL,
                eviction_rate TEXT,
                product_name  TEXT,
                last_updated  TEXT NOT NULL,
                PRIMARY KEY (vm_size, region)
            );
            CREATE INDEX IF NOT EXISTS idx_region ON spot_prices(region);
            CREATE INDEX IF NOT EXISTS idx_family ON spot_prices(vm_family);
            CREATE INDEX IF NOT EXISTS idx_vcpus  ON spot_prices(vcpus);

            CREATE TABLE IF NOT EXISTS collection_log (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                region     TEXT NOT NULL,
                status     TEXT NOT NULL,
                message    TEXT,
                started_at TEXT NOT NULL,
                finished_at TEXT
            );

            CREATE TABLE IF NOT EXISTS price_history (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                vm_size        TEXT NOT NULL,
                region         TEXT NOT NULL,
                spot_price     REAL NOT NULL,
                ondemand_price REAL,
                recorded_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_ph_vm_region ON price_history(vm_size, region);
            CREATE INDEX IF NOT EXISTS idx_ph_recorded  ON price_history(recorded_at);

            CREATE TRIGGER IF NOT EXISTS record_price_change
            AFTER UPDATE ON spot_prices
            WHEN OLD.spot_price != NEW.spot_price
            BEGIN
                INSERT INTO price_history (vm_size, region, spot_price, ondemand_price)
                VALUES (NEW.vm_size, NEW.region, NEW.spot_price, NEW.ondemand_price);
            END;

            CREATE TRIGGER IF NOT EXISTS record_initial_price
            AFTER INSERT ON spot_prices
            BEGIN
                INSERT INTO price_history (vm_size, region, spot_price, ondemand_price)
                VALUES (NEW.vm_size, NEW.region, NEW.spot_price, NEW.ondemand_price);
            END;
        """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertPrices(IEnumerable<SpotVmPrice> prices)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO spot_prices (vm_size, region, spot_price, ondemand_price, vcpus, memory_gb, vm_family, friendly_cat, eviction_rate, last_updated)
            VALUES ($vm, $reg, $spot, $od, $cpu, $mem, $fam, $cat, $ev, $upd)
            ON CONFLICT(vm_size, region) DO UPDATE SET
                spot_price = excluded.spot_price,
                ondemand_price = excluded.ondemand_price,
                vcpus = excluded.vcpus,
                memory_gb = excluded.memory_gb,
                vm_family = excluded.vm_family,
                friendly_cat = excluded.friendly_cat,
                eviction_rate = COALESCE(excluded.eviction_rate, spot_prices.eviction_rate),
                last_updated = excluded.last_updated;
        """;

        var pVm = cmd.CreateParameter(); pVm.ParameterName = "$vm"; cmd.Parameters.Add(pVm);
        var pReg = cmd.CreateParameter(); pReg.ParameterName = "$reg"; cmd.Parameters.Add(pReg);
        var pSpot = cmd.CreateParameter(); pSpot.ParameterName = "$spot"; cmd.Parameters.Add(pSpot);
        var pOd = cmd.CreateParameter(); pOd.ParameterName = "$od"; cmd.Parameters.Add(pOd);
        var pCpu = cmd.CreateParameter(); pCpu.ParameterName = "$cpu"; cmd.Parameters.Add(pCpu);
        var pMem = cmd.CreateParameter(); pMem.ParameterName = "$mem"; cmd.Parameters.Add(pMem);
        var pFam = cmd.CreateParameter(); pFam.ParameterName = "$fam"; cmd.Parameters.Add(pFam);
        var pCat = cmd.CreateParameter(); pCat.ParameterName = "$cat"; cmd.Parameters.Add(pCat);
        var pEv = cmd.CreateParameter(); pEv.ParameterName = "$ev"; cmd.Parameters.Add(pEv);
        var pUpd = cmd.CreateParameter(); pUpd.ParameterName = "$upd"; cmd.Parameters.Add(pUpd);

        foreach (var p in prices)
        {
            pVm.Value = p.VmSize;
            pReg.Value = p.Region;
            pSpot.Value = (double)p.SpotPricePerHour;
            pOd.Value = (double)p.OnDemandPricePerHour;
            pCpu.Value = p.VCpus;
            pMem.Value = (double)p.MemoryGB;
            pFam.Value = p.VmFamily;
            pCat.Value = p.FriendlyCategory;
            pEv.Value = (object?)p.EvictionRate ?? DBNull.Value;
            pUpd.Value = p.LastUpdated.ToString("o");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpdateEvictionRates(Dictionary<(string vmSize, string region), string> rates)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE spot_prices SET eviction_rate = $ev WHERE vm_size = $vm AND region = $reg;";

        var pVm = cmd.CreateParameter(); pVm.ParameterName = "$vm"; cmd.Parameters.Add(pVm);
        var pReg = cmd.CreateParameter(); pReg.ParameterName = "$reg"; cmd.Parameters.Add(pReg);
        var pEv = cmd.CreateParameter(); pEv.ParameterName = "$ev"; cmd.Parameters.Add(pEv);

        foreach (var ((vmSize, region), rate) in rates)
        {
            pVm.Value = vmSize;
            pReg.Value = region;
            pEv.Value = rate;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<SpotVmPrice> QueryPrices(
        IReadOnlyList<string>? regions = null,
        IReadOnlyList<string>? families = null,
        int? minVCpus = null,
        int? maxVCpus = null,
        decimal? minMemory = null,
        decimal? maxMemory = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        if (regions is { Count: > 0 })
        {
            var placeholders = string.Join(",", regions.Select((_, i) => $"$r{i}"));
            where.Add($"region IN ({placeholders})");
            for (var i = 0; i < regions.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"$r{i}";
                p.Value = regions[i];
                cmd.Parameters.Add(p);
            }
        }
        if (families is { Count: > 0 })
        {
            var placeholders = string.Join(",", families.Select((_, i) => $"$f{i}"));
            where.Add($"friendly_cat IN ({placeholders})");
            for (var i = 0; i < families.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"$f{i}";
                p.Value = families[i];
                cmd.Parameters.Add(p);
            }
        }
        if (minVCpus.HasValue) { where.Add("vcpus >= $minCpu"); var p = cmd.CreateParameter(); p.ParameterName = "$minCpu"; p.Value = minVCpus.Value; cmd.Parameters.Add(p); }
        if (maxVCpus.HasValue) { where.Add("vcpus <= $maxCpu"); var p = cmd.CreateParameter(); p.ParameterName = "$maxCpu"; p.Value = maxVCpus.Value; cmd.Parameters.Add(p); }
        if (minMemory.HasValue) { where.Add("memory_gb >= $minMem"); var p = cmd.CreateParameter(); p.ParameterName = "$minMem"; p.Value = (double)minMemory.Value; cmd.Parameters.Add(p); }
        if (maxMemory.HasValue) { where.Add("memory_gb <= $maxMem"); var p = cmd.CreateParameter(); p.ParameterName = "$maxMem"; p.Value = (double)maxMemory.Value; cmd.Parameters.Add(p); }

        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"SELECT vm_size, region, spot_price, ondemand_price, vcpus, memory_gb, vm_family, friendly_cat, eviction_rate, last_updated FROM spot_prices {clause} ORDER BY spot_price ASC;";

        var results = new List<SpotVmPrice>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SpotVmPrice
            {
                VmSize = reader.GetString(0),
                Region = reader.GetString(1),
                SpotPricePerHour = (decimal)reader.GetDouble(2),
                OnDemandPricePerHour = (decimal)reader.GetDouble(3),
                VCpus = reader.GetInt32(4),
                MemoryGB = (decimal)reader.GetDouble(5),
                VmFamily = reader.GetString(6),
                FriendlyCategory = reader.GetString(7),
                EvictionRate = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastUpdated = DateTime.Parse(reader.GetString(9))
            });
        }
        return results;
    }

    public List<string> GetRegions()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT region FROM spot_prices ORDER BY region;";
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public List<string> GetFamilies()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT friendly_cat FROM spot_prices ORDER BY friendly_cat;";
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public int GetPriceCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM spot_prices;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public DateTime? GetLastUpdate()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_updated) FROM spot_prices;";
        var result = cmd.ExecuteScalar();
        return result is string s ? DateTime.Parse(s) : null;
    }

    /// <summary>Average spot price per vCPU grouped by category and region.</summary>
    public List<ChartDataPoint> GetPricePerVCpuByCategory(
        IReadOnlyList<string>? regions = null,
        int? minVCpus = null, int? maxVCpus = null,
        decimal? minMemory = null, decimal? maxMemory = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string> { "vcpus > 0" };
        ApplyFilters(cmd, where, regions, null, minVCpus, maxVCpus, minMemory, maxMemory);
        var clause = "WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"""
            SELECT friendly_cat, region,
                   AVG(spot_price / vcpus) as avg_price_per_vcpu,
                   AVG(ondemand_price / vcpus) as avg_od_per_vcpu,
                   COUNT(*) as cnt
            FROM spot_prices {clause}
            GROUP BY friendly_cat, region
            ORDER BY friendly_cat, region;
        """;
        var results = new List<ChartDataPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ChartDataPoint(
                reader.GetString(0), reader.GetString(1),
                (decimal)reader.GetDouble(2), (decimal)reader.GetDouble(3),
                reader.GetInt32(4)));
        }
        return results;
    }

    /// <summary>Savings distribution: count of VMs in each savings bracket.</summary>
    public List<(string Bracket, int Count)> GetSavingsDistribution(
        IReadOnlyList<string>? regions = null,
        IReadOnlyList<string>? families = null,
        int? minVCpus = null, int? maxVCpus = null,
        decimal? minMemory = null, decimal? maxMemory = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string> { "ondemand_price > 0" };
        ApplyFilters(cmd, where, regions, families, minVCpus, maxVCpus, minMemory, maxMemory);
        var clause = "WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"""
            SELECT
              CASE
                WHEN (1.0 - spot_price / ondemand_price) >= 0.9 THEN '90-100%'
                WHEN (1.0 - spot_price / ondemand_price) >= 0.8 THEN '80-90%'
                WHEN (1.0 - spot_price / ondemand_price) >= 0.7 THEN '70-80%'
                WHEN (1.0 - spot_price / ondemand_price) >= 0.6 THEN '60-70%'
                WHEN (1.0 - spot_price / ondemand_price) >= 0.5 THEN '50-60%'
                WHEN (1.0 - spot_price / ondemand_price) >= 0.4 THEN '40-50%'
                ELSE '<40%'
              END as bracket,
              COUNT(*) as cnt
            FROM spot_prices {clause}
            GROUP BY bracket
            ORDER BY bracket;
        """;
        var results = new List<(string, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    /// <summary>Average spot price by region for a given vCPU count.</summary>
    public List<(string Region, decimal AvgSpot, decimal AvgOnDemand, int Count)> GetAvgPriceByRegion(
        int vcpus,
        IReadOnlyList<string>? families = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string> { "vcpus = $cpu" };
        var pCpu = cmd.CreateParameter(); pCpu.ParameterName = "$cpu"; pCpu.Value = vcpus; cmd.Parameters.Add(pCpu);
        ApplyFilters(cmd, where, null, families, null, null, null, null);
        var clause = "WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"""
            SELECT region, AVG(spot_price), AVG(ondemand_price), COUNT(*)
            FROM spot_prices {clause}
            GROUP BY region ORDER BY AVG(spot_price);
        """;
        var results = new List<(string, decimal, decimal, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), (decimal)reader.GetDouble(1), (decimal)reader.GetDouble(2), reader.GetInt32(3)));
        return results;
    }

    /// <summary>Eviction rate distribution across all collected VMs.</summary>
    public List<(string Rate, int Count)> GetEvictionDistribution(
        IReadOnlyList<string>? regions = null,
        IReadOnlyList<string>? families = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        ApplyFilters(cmd, where, regions, families, null, null, null, null);
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT COALESCE(eviction_rate, 'Unknown'), COUNT(*)
            FROM spot_prices {clause}
            GROUP BY eviction_rate ORDER BY eviction_rate;
        """;
        var results = new List<(string, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    /// <summary>Distinct vCPU counts available in the database.</summary>
    public List<int> GetDistinctVCpus()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT vcpus FROM spot_prices WHERE vcpus > 0 ORDER BY vcpus;";
        var results = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetInt32(0));
        return results;
    }

    void ApplyFilters(SqliteCommand cmd, List<string> where,
        IReadOnlyList<string>? regions, IReadOnlyList<string>? families,
        int? minVCpus, int? maxVCpus, decimal? minMemory, decimal? maxMemory)
    {
        if (regions is { Count: > 0 })
        {
            var ph = string.Join(",", regions.Select((_, i) => $"$ar{i}"));
            where.Add($"region IN ({ph})");
            for (var i = 0; i < regions.Count; i++)
            { var p = cmd.CreateParameter(); p.ParameterName = $"$ar{i}"; p.Value = regions[i]; cmd.Parameters.Add(p); }
        }
        if (families is { Count: > 0 })
        {
            var ph = string.Join(",", families.Select((_, i) => $"$af{i}"));
            where.Add($"friendly_cat IN ({ph})");
            for (var i = 0; i < families.Count; i++)
            { var p = cmd.CreateParameter(); p.ParameterName = $"$af{i}"; p.Value = families[i]; cmd.Parameters.Add(p); }
        }
        if (minVCpus.HasValue) { where.Add("vcpus >= $mnC"); var p = cmd.CreateParameter(); p.ParameterName = "$mnC"; p.Value = minVCpus.Value; cmd.Parameters.Add(p); }
        if (maxVCpus.HasValue) { where.Add("vcpus <= $mxC"); var p = cmd.CreateParameter(); p.ParameterName = "$mxC"; p.Value = maxVCpus.Value; cmd.Parameters.Add(p); }
        if (minMemory.HasValue) { where.Add("memory_gb >= $mnM"); var p = cmd.CreateParameter(); p.ParameterName = "$mnM"; p.Value = (double)minMemory.Value; cmd.Parameters.Add(p); }
        if (maxMemory.HasValue) { where.Add("memory_gb <= $mxM"); var p = cmd.CreateParameter(); p.ParameterName = "$mxM"; p.Value = (double)maxMemory.Value; cmd.Parameters.Add(p); }
    }

    public void Dispose() => _conn.Dispose();

    // ── Trading / history query methods ──────────────────────────────────

    public List<PriceHistoryPoint> GetPriceHistory(string vmSize, string region, int days = 30)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT recorded_at, spot_price
            FROM price_history
            WHERE vm_size = $vm AND region = $reg
              AND recorded_at > datetime('now', '-' || $days || ' days')
            ORDER BY recorded_at ASC;
        """;
        var pVm = cmd.CreateParameter(); pVm.ParameterName = "$vm"; pVm.Value = vmSize; cmd.Parameters.Add(pVm);
        var pReg = cmd.CreateParameter(); pReg.ParameterName = "$reg"; pReg.Value = region; cmd.Parameters.Add(pReg);
        var pDays = cmd.CreateParameter(); pDays.ParameterName = "$days"; pDays.Value = days; cmd.Parameters.Add(pDays);

        var results = new List<PriceHistoryPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new PriceHistoryPoint(DateTime.Parse(reader.GetString(0)), (decimal)reader.GetDouble(1)));
        return results;
    }

    public List<BigMover> GetBiggestMovers(int hours = 24, int limit = 20)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH range AS (
                SELECT vm_size, region,
                       MIN(recorded_at) AS first_ts,
                       MAX(recorded_at) AS last_ts
                FROM price_history
                WHERE recorded_at > datetime('now', '-' || $hours || ' hours')
                GROUP BY vm_size, region
            )
            SELECT r.vm_size, r.region,
                   sp.friendly_cat, sp.vcpus, sp.memory_gb,
                   ph_last.spot_price  AS new_price,
                   ph_first.spot_price AS old_price,
                   (ph_last.spot_price - ph_first.spot_price) / ph_first.spot_price * 100.0 AS change_pct
            FROM range r
            JOIN price_history ph_first
              ON ph_first.vm_size = r.vm_size AND ph_first.region = r.region AND ph_first.recorded_at = r.first_ts
            JOIN price_history ph_last
              ON ph_last.vm_size  = r.vm_size AND ph_last.region  = r.region AND ph_last.recorded_at  = r.last_ts
            JOIN spot_prices sp
              ON r.vm_size = sp.vm_size AND r.region = sp.region
            WHERE ph_first.spot_price != ph_last.spot_price
            ORDER BY ABS(change_pct) DESC
            LIMIT $limit;
        """;
        var pH = cmd.CreateParameter(); pH.ParameterName = "$hours"; pH.Value = hours; cmd.Parameters.Add(pH);
        var pL = cmd.CreateParameter(); pL.ParameterName = "$limit"; pL.Value = limit; cmd.Parameters.Add(pL);

        var results = new List<BigMover>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new BigMover(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), (decimal)reader.GetDouble(4),
                (decimal)reader.GetDouble(5), (decimal)reader.GetDouble(6),
                (decimal)reader.GetDouble(7)));
        return results;
    }

    public List<HeatmapCell> GetHeatmapData()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT friendly_cat, region,
                   AVG((1.0 - spot_price / ondemand_price) * 100.0) AS avg_savings_pct,
                   AVG(spot_price)                                   AS avg_spot,
                   COUNT(*)                                          AS vm_count
            FROM spot_prices
            WHERE ondemand_price > 0
            GROUP BY friendly_cat, region
            ORDER BY friendly_cat, region;
        """;
        var results = new List<HeatmapCell>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new HeatmapCell(
                reader.GetString(0), reader.GetString(1),
                (decimal)reader.GetDouble(2), (decimal)reader.GetDouble(3),
                reader.GetInt32(4)));
        return results;
    }

    public List<CalculatorResult> GetCalculatorResults(
        int minVcpus, decimal minMemoryGb, decimal hoursPerMonth,
        bool lowRiskOnly, IReadOnlyList<string>? regions = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>
        {
            "vcpus >= $minCpu",
            "memory_gb >= $minMem",
            "ondemand_price > 0"
        };
        var pCpu = cmd.CreateParameter(); pCpu.ParameterName = "$minCpu"; pCpu.Value = minVcpus; cmd.Parameters.Add(pCpu);
        var pMem = cmd.CreateParameter(); pMem.ParameterName = "$minMem"; pMem.Value = (double)minMemoryGb; cmd.Parameters.Add(pMem);

        if (lowRiskOnly)
            where.Add("(eviction_rate IN ('0-5','5-10') OR eviction_rate IS NULL)");

        if (regions is { Count: > 0 })
        {
            var ph = string.Join(",", regions.Select((_, i) => $"$cr{i}"));
            where.Add($"region IN ({ph})");
            for (var i = 0; i < regions.Count; i++)
            { var p = cmd.CreateParameter(); p.ParameterName = $"$cr{i}"; p.Value = regions[i]; cmd.Parameters.Add(p); }
        }

        var clause = "WHERE " + string.Join(" AND ", where);
        cmd.CommandText = $"""
            SELECT vm_size, region, friendly_cat, vcpus, memory_gb,
                   spot_price, ondemand_price, eviction_rate,
                   (1.0 - spot_price / ondemand_price) * 100.0 AS savings_pct
            FROM spot_prices {clause}
            ORDER BY savings_pct DESC
            LIMIT 200;
        """;

        var rows = new List<(string vm, string reg, string cat, int cpu, decimal mem,
            decimal spot, decimal od, string? ev, decimal savPct)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), (decimal)reader.GetDouble(4),
                (decimal)reader.GetDouble(5), (decimal)reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                (decimal)reader.GetDouble(8)));

        // Score each result and return top 20
        return rows
            .Select(r =>
            {
                var evScore = r.ev switch
                {
                    "0-5" => 1.0m, "5-10" => 0.8m, "10-15" => 0.6m,
                    "15-20" => 0.4m, "20+" => 0.2m, _ => 0.5m
                };
                var cpuEff = Math.Clamp((decimal)minVcpus / r.cpu, 0.1m, 1.0m);
                var memEff = Math.Clamp(minMemoryGb / r.mem, 0.1m, 1.0m);
                var specEff = (cpuEff + memEff) / 2m;
                var score = (r.savPct / 100m) * 0.5m + evScore * 0.3m + specEff * 0.2m;
                var monthlySpot = PriceCalculations.MonthlyCost(r.spot * hoursPerMonth / 730m);
                var monthlyOd   = PriceCalculations.MonthlyCost(r.od   * hoursPerMonth / 730m);
                return new CalculatorResult(r.vm, r.reg, r.cat, r.cpu, r.mem,
                    r.spot, r.od, r.savPct, r.ev, score,
                    monthlySpot, monthlyOd, monthlyOd - monthlySpot);
            })
            .OrderByDescending(r => r.Score)
            .Take(20)
            .ToList();
    }

    public int GetPricesChangedInHours(int hours = 1)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT vm_size || '|' || region) FROM price_history WHERE recorded_at > datetime('now', '-' || $hours || ' hours');";
        var p = cmd.CreateParameter(); p.ParameterName = "$hours"; p.Value = hours; cmd.Parameters.Add(p);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void CleanupOldHistory(int retentionDays = 90)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM price_history WHERE recorded_at < datetime('now', '-' || $days || ' days');";
        var p = cmd.CreateParameter(); p.ParameterName = "$days"; p.Value = retentionDays; cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
        using var vacuum = _conn.CreateCommand();
        vacuum.CommandText = "PRAGMA incremental_vacuum;";
        vacuum.ExecuteNonQuery();
    }
}
