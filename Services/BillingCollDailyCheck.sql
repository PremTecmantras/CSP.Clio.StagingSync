USE [EKPBI_New];
GO

DECLARE @Year int = NULL;        -- NULL = ALL years
DECLARE @Scale tinyint = 0;      -- single source of truth for decimals

DECLARE @Tolerance decimal(38,10) =
    (POWER(CAST(10.0 AS float), -@Scale) / 2.0);

DECLARE @DateFrom date = CASE WHEN @Year IS NULL THEN '19000101' ELSE DATEFROMPARTS(@Year,1,1) END;
DECLARE @DateTo   date = CASE WHEN @Year IS NULL THEN '99991231' ELSE DATEFROMPARTS(@Year+1,1,1) END;

------------------------------------------------------------
-- RESULT SET 1: Totals variances by CompanyName
------------------------------------------------------------

;WITH
ValidMatters AS (
    SELECT
        m.InternalID AS MatterID,
        m.CompanyName
    FROM dbo.VW_Matters_Detailed m
    WHERE LTRIM(RTRIM(CAST(m.PracticeAreaName AS nvarchar(4000)))) IN (
        N'Corporate, Commercial and M&A',
        N'Insurance/Reinsurance',
        N'Dispute Resolution',
        N'Projects',
        N'Construction',
		N'Technology, Media, and Telecommunications'
    )
),

Control AS (
    SELECT
        'NetCollectionUSD' AS Metric,
        vm.CompanyName,
        SUM(CAST(nc.NetCollectionUSD AS decimal(38,10))) AS ControlTotal
    FROM dbo.VW_MegaTable_Net_Collection_Table nc
    INNER JOIN ValidMatters vm ON vm.MatterID = nc.MatterID
    WHERE nc.AllocationDate >= @DateFrom AND nc.AllocationDate < @DateTo
    GROUP BY vm.CompanyName

    UNION ALL

    SELECT
        'ThirdPartyCostUSD',
        vm.CompanyName,
        SUM(CAST(tpc.PaymentAmountUSD AS decimal(38,10)))
    FROM dbo.VW_3rdPartyCost_Table tpc
    INNER JOIN ValidMatters vm ON vm.MatterID = tpc.InternalID
    WHERE tpc.PaymentDate >= @DateFrom AND tpc.PaymentDate < @DateTo
    GROUP BY vm.CompanyName

    UNION ALL

    SELECT
        'BilledUSD',
        vm.CompanyName,
        SUM(CAST(bt.BilledTimeUSD AS decimal(38,10)))
    FROM dbo.VW_MegaTable_BilledTime_Table bt
    INNER JOIN ValidMatters vm ON vm.MatterID = bt.MatterID
    WHERE bt.Bill_Issue_Date >= @DateFrom AND bt.Bill_Issue_Date < @DateTo
    GROUP BY vm.CompanyName

    UNION ALL

    SELECT
        'CreditNotesUSD',
        vm.CompanyName,
        -SUM(ABS(CAST(cm.NetCreditMemoUSD AS decimal(38,10))))
    FROM dbo.VW_MegaTable_NetCreditMemo_Table cm
    INNER JOIN ValidMatters vm ON vm.MatterID = cm.MatterID
    WHERE cm.AllocationDate >= @DateFrom AND cm.AllocationDate < @DateTo
    GROUP BY vm.CompanyName
),

PartnerFact AS (
    SELECT
        'NetCollectionUSD' AS Metric,
        vm.CompanyName,
        SUM(CAST(pf.NetCollectionUSD AS decimal(38,10))) AS PartnerFactTotal
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'COLLECTION'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY vm.CompanyName

    UNION ALL

    SELECT
        'ThirdPartyCostUSD',
        vm.CompanyName,
        SUM(CAST(pf.ThirdPartyCostUSD AS decimal(38,10)))
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'COLLECTION'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY vm.CompanyName

    UNION ALL

    SELECT
        'BilledUSD',
        vm.CompanyName,
        SUM(CAST(pf.BilledUSD AS decimal(38,10)))
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'BILLING'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY vm.CompanyName

    UNION ALL

    SELECT
        'CreditNotesUSD',
        vm.CompanyName,
        -SUM(ABS(CAST(pf.CreditNotesUSD AS decimal(38,10))))
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'CREDIT NOTES'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY vm.CompanyName
),

Aligned AS (
    SELECT
        COALESCE(c.Metric, p.Metric) AS Metric,
        COALESCE(c.CompanyName, p.CompanyName) AS CompanyName,
        ROUND(COALESCE(c.ControlTotal, 0), @Scale) AS ControlTotal,
        ROUND(COALESCE(p.PartnerFactTotal, 0), @Scale) AS PartnerFactTotal
    FROM Control c
    FULL OUTER JOIN PartnerFact p
        ON p.Metric = c.Metric
       AND ISNULL(p.CompanyName, '') = ISNULL(c.CompanyName, '')
)

SELECT
    Metric,
    CompanyName,
    ControlTotal,
    PartnerFactTotal,
    PartnerFactTotal - ControlTotal AS Variance,
    CASE
        WHEN ControlTotal = 0 THEN NULL
        ELSE (PartnerFactTotal - ControlTotal) / NULLIF(ControlTotal, 0)
    END AS VariancePct
FROM Aligned
ORDER BY
    CompanyName,
    CASE Metric
        WHEN 'BilledUSD' THEN 1
        WHEN 'CreditNotesUSD' THEN 2
        WHEN 'NetCollectionUSD' THEN 3
        WHEN 'ThirdPartyCostUSD' THEN 4
        ELSE 99
    END;

------------------------------------------------------------
-- RESULT SET 2: MatterID variances with CompanyName
------------------------------------------------------------

;WITH
ValidMatters AS (
    SELECT
        m.InternalID AS MatterID,
        m.CompanyName
    FROM dbo.VW_Matters_Detailed m
    WHERE LTRIM(RTRIM(CAST(m.PracticeAreaName AS nvarchar(4000)))) IN (
        N'Corporate, Commercial and M&A',
        N'Insurance/Reinsurance',
        N'Dispute Resolution',
        N'Projects',
        N'Construction',
		N'Technology, Media, and Telecommunications')
),

Control_Matter AS (
    SELECT
        'NetCollectionUSD' AS Metric,
        CAST(nc.MatterID AS bigint) AS MatterID,
        vm.CompanyName,
        SUM(CAST(nc.NetCollectionUSD AS decimal(38,10))) AS ControlTotal
    FROM dbo.VW_MegaTable_Net_Collection_Table nc
    INNER JOIN ValidMatters vm ON vm.MatterID = nc.MatterID
    WHERE nc.AllocationDate >= @DateFrom AND nc.AllocationDate < @DateTo
    GROUP BY nc.MatterID, vm.CompanyName

    UNION ALL

    SELECT
        'ThirdPartyCostUSD',
        CAST(tpc.InternalID AS bigint),
        vm.CompanyName,
        SUM(CAST(tpc.PaymentAmountUSD AS decimal(38,10)))
    FROM dbo.VW_3rdPartyCost_Table tpc
    INNER JOIN ValidMatters vm ON vm.MatterID = tpc.InternalID
    WHERE tpc.PaymentDate >= @DateFrom AND tpc.PaymentDate < @DateTo
    GROUP BY tpc.InternalID, vm.CompanyName

    UNION ALL

    SELECT
        'BilledUSD',
        CAST(bt.MatterID AS bigint),
        vm.CompanyName,
        SUM(CAST(bt.BilledTimeUSD AS decimal(38,10)))
    FROM dbo.VW_MegaTable_BilledTime_Table bt
    INNER JOIN ValidMatters vm ON vm.MatterID = bt.MatterID
    WHERE bt.Bill_Issue_Date >= @DateFrom AND bt.Bill_Issue_Date < @DateTo
    GROUP BY bt.MatterID, vm.CompanyName

    UNION ALL

    SELECT
        'CreditNotesUSD',
        CAST(cm.MatterID AS bigint),
        vm.CompanyName,
        -SUM(ABS(CAST(cm.NetCreditMemoUSD AS decimal(38,10))))
    FROM dbo.VW_MegaTable_NetCreditMemo_Table cm
    INNER JOIN ValidMatters vm ON vm.MatterID = cm.MatterID
    WHERE cm.AllocationDate >= @DateFrom AND cm.AllocationDate < @DateTo
    GROUP BY cm.MatterID, vm.CompanyName
),

PartnerFact_Matter AS (
    SELECT
        'NetCollectionUSD' AS Metric,
        CAST(pf.MatterID AS bigint) AS MatterID,
        vm.CompanyName,
        SUM(CAST(pf.NetCollectionUSD AS decimal(38,10))) AS PartnerFactTotal
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'COLLECTION'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY pf.MatterID, vm.CompanyName

    UNION ALL

    SELECT
        'ThirdPartyCostUSD',
        CAST(pf.MatterID AS bigint),
        vm.CompanyName,
        SUM(CAST(pf.ThirdPartyCostUSD AS decimal(38,10)))
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'COLLECTION'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY pf.MatterID, vm.CompanyName

    UNION ALL

    SELECT
        'BilledUSD',
        CAST(pf.MatterID AS bigint),
        vm.CompanyName,
        SUM(CAST(pf.BilledUSD AS decimal(38,10)))
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'BILLING'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY pf.MatterID, vm.CompanyName

    UNION ALL

    SELECT
        'CreditNotesUSD',
        CAST(pf.MatterID AS bigint),
        vm.CompanyName,
        -SUM(ABS(CAST(pf.CreditNotesUSD AS decimal(38,10))))
    FROM dbo.VW_PartnerFact_Billing_Collections_Table pf
    INNER JOIN ValidMatters vm ON vm.MatterID = pf.MatterID
    WHERE pf.TransactionType = 'CREDIT NOTES'
      AND pf.FactDate >= @DateFrom AND pf.FactDate < @DateTo
    GROUP BY pf.MatterID, vm.CompanyName
),

Matter_Aligned AS (
    SELECT
        COALESCE(c.Metric, p.Metric) AS Metric,
        COALESCE(c.CompanyName, p.CompanyName) AS CompanyName,
        COALESCE(c.MatterID, p.MatterID) AS MatterID,
        ROUND(COALESCE(c.ControlTotal, 0), @Scale) AS ControlTotal,
        ROUND(COALESCE(p.PartnerFactTotal, 0), @Scale) AS PartnerFactTotal
    FROM Control_Matter c
    FULL OUTER JOIN PartnerFact_Matter p
        ON p.Metric = c.Metric
       AND p.MatterID = c.MatterID
),

Matter_Variances AS (
    SELECT
        Metric,
        CompanyName,
        MatterID,
        ControlTotal,
        PartnerFactTotal,
        PartnerFactTotal - ControlTotal AS Variance,
        CASE
            WHEN ControlTotal = 0 THEN NULL
            ELSE (PartnerFactTotal - ControlTotal) / NULLIF(ControlTotal, 0)
        END AS VariancePct
    FROM Matter_Aligned
)

SELECT
    Metric,
    CompanyName,
    MatterID,
    ControlTotal,
    PartnerFactTotal,
    Variance,
    VariancePct
FROM Matter_Variances
WHERE ABS(Variance) >= @Tolerance
ORDER BY
    CompanyName,
    CASE Metric
        WHEN 'BilledUSD' THEN 1
        WHEN 'CreditNotesUSD' THEN 2
        WHEN 'NetCollectionUSD' THEN 3
        WHEN 'ThirdPartyCostUSD' THEN 4
        ELSE 99
    END,
    ABS(Variance) DESC,
    MatterID;