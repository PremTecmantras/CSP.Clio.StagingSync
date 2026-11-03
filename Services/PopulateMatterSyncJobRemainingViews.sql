USE [EKPBI_New_Dev];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EKPBI_New_Dev'
	THROW 51050, 'Wrong database. Review the USE statement before running this script.', 1;

IF OBJECT_ID(N'dbo.PopulateTables', N'P') IS NULL
	THROW 51051, 'dbo.PopulateTables does not exist in the selected database.', 1;

PRINT N'Populating VW_MegaTable_Net_Collection';
EXEC dbo.PopulateTables
	@updateAll = 0,
	@specificViewName = N'VW_MegaTable_Net_Collection',
	@EverySync = 0;

PRINT N'Populating VW_BankTransactions';
EXEC dbo.PopulateTables
	@updateAll = 0,
	@specificViewName = N'VW_BankTransactions',
	@EverySync = 0;

PRINT N'Populating VW_MegaTable_Net_Billed_Time';
EXEC dbo.PopulateTables
	@updateAll = 0,
	@specificViewName = N'VW_MegaTable_Net_Billed_Time',
	@EverySync = 0;
