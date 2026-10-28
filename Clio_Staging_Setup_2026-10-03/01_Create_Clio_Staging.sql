/* STAGING-ONLY BOOTSTRAP. SQL Server 2019+; run as DBA in SSMS.
   Reads EKPBI_New_Dev metadata and explicit non-secret configuration. Does not write to it.
   Creates a NEW Clio_Staging database; refuses an existing database.
   No SQL logins, scheduled jobs, credentials, production publish or automatic activation.
   Uses instance default data/log paths. Check free space first; defaults initially allocate
   an empty database, not the production database's 11+ GB allocation.
   A schema-drift failure leaves an empty new database for DBA review, with no automatic drop.
   This is ONE batch: do not split or execute selected fragments.
*/
USE [master];
SET NOCOUNT ON;
IF @@TRANCOUNT<>0 THROW 51000,'Run outside any existing transaction.',1;
IF DB_ID(N'EKPBI_New_Dev') IS NULL THROW 51000,'EKPBI_New_Dev is not on this instance.',1;
IF DB_ID(N'Clio_Staging') IS NOT NULL THROW 51000,'Clio_Staging already exists; nothing changed. Inspect it before rerunning.',1;
IF CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))<15 THROW 51000,'This baseline requires SQL Server 2019 or later.',1;
IF HAS_DBACCESS(N'EKPBI_New_Dev')<>1 THROW 51000,'Cannot read EKPBI_New_Dev.',1;
DECLARE @collation sysname=CONVERT(sysname,DATABASEPROPERTYEX(N'EKPBI_New_Dev',N'Collation'));
DECLARE @create nvarchar(max)=N'CREATE DATABASE [Clio_Staging] COLLATE '+@collation+N';';
EXEC sys.sp_executesql @create;
ALTER DATABASE [Clio_Staging] SET COMPATIBILITY_LEVEL=150;
ALTER DATABASE [Clio_Staging] SET RECOVERY SIMPLE;
ALTER DATABASE [Clio_Staging] SET AUTO_CLOSE OFF;
ALTER DATABASE [Clio_Staging] SET AUTO_SHRINK OFF;
ALTER DATABASE [Clio_Staging] SET TRUSTWORTHY OFF;
ALTER DATABASE [Clio_Staging] SET DB_CHAINING OFF;
ALTER DATABASE [Clio_Staging] SET PAGE_VERIFY CHECKSUM;
DECLARE @setup nvarchar(max)=
N'USE [Clio_Staging];
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
BEGIN TRY
BEGIN TRANSACTION;
CREATE TABLE [dbo].[Contacts](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [ntext] NULL,
	[Redacted] [bigint] NOT NULL,
	[Avatar] [bigint] NULL,
	[Clio_Connect_Email] [ntext] NULL,
	[Company] [bigint] NULL,
	[First_Name] [text] NULL,
	[Folder] [bigint] NULL,
	[Initials] [ntext] NULL,
	[Last_Name] [ntext] NULL,
	[Middle_Name] [ntext] NULL,
	[Name] [ntext] NULL,
	[Payment_Profile] [bigint] NULL,
	[Prefix] [ntext] NULL,
	[Primary_Email_Address] [ntext] NULL,
	[Primary_Phone_Number] [ntext] NULL,
	[Title] [ntext] NULL,
	[Type] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[ContactType] [nvarchar](150) NULL,
	[Clio_Internal_ID] [int] NULL,
	[Co_Counsel_Rate] [decimal](18, 2) NULL,
 CONSTRAINT [PK_Contacts] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[CustomFields](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [text] NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [ntext] NULL,
	[Redacted] [bigint] NOT NULL,
	[Deleted] [bigint] NOT NULL,
	[Display_Order] [text] NULL,
	[Displayed] [bigint] NOT NULL,
	[Field_Type] [bigint] NOT NULL,
	[Name] [ntext] NULL,
	[Parent_Type] [bigint] NOT NULL,
	[Required] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_CustomFields] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Folders](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Contact] [bigint] NULL,
	[Creator] [bigint] NULL,
	[Deleted_At] [text] NULL,
	[Document_Category] [bigint] NULL,
	[Locked] [bigint] NOT NULL,
	[Matter] [bigint] NULL,
	[Name] [text] NULL,
	[Parent] [bigint] NULL,
	[Root] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Type] [nvarchar](150) NOT NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Folders] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[PracticeAreas](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Code] [text] NULL,
	[Name] [text] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_PracticeAreas] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Relationships](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Contact] [bigint] NULL,
	[Description] [text] NULL,
	[Matter] [bigint] NULL,
	[Clio_Internal_ID] [int] NULL,
	[CompanyID] [uniqueidentifier] NULL,
 CONSTRAINT [PK_Relationships] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TaskTypes](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Deleted_At] [text] NULL,
	[Name] [text] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_TaskTypes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Users](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[IsWhoAmI] [bigint] NOT NULL,
	[Account] [bigint] NULL,
	[Account_Owner] [bigint] NOT NULL,
	[Avatar] [bigint] NULL,
	[Clio_Connect] [bigint] NOT NULL,
	[Court_Rules_Default_Attendee] [bigint] NOT NULL,
	[Default_Activity_Description] [bigint] NULL,
	[Default_Calendar_Id] [bigint] NULL,
	[EMail] [nvarchar](150) NULL,
	[Enabled] [bigint] NOT NULL,
	[First_Name] [nvarchar](150) NULL,
	[Initials] [nvarchar](150) NULL,
	[Job_Title] [bigint] NULL,
	[Last_Name] [nvarchar](150) NULL,
	[Name] [nvarchar](150) NULL,
	[Phone_Number] [text] NULL,
	[Rate] [text] NOT NULL,
	[Subscription_Type] [bigint] NOT NULL,
	[Time_Zone] [text] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Activities](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [ntext] NULL,
	[Redacted] [bigint] NOT NULL,
	[Activity_Description] [bigint] NULL,
	[Bill] [bigint] NULL,
	[Billed] [bigint] NOT NULL,
	[Calendar_Entry] [bigint] NULL,
	[Communication] [bigint] NULL,
	[Contact_Note] [bigint] NULL,
	[Contingency_Fee] [bigint] NOT NULL,
	[Date] [text] NULL,
	[Expense_Category] [bigint] NULL,
	[Flat_Rate] [bigint] NOT NULL,
	[Matter] [bigint] NULL,
	[Matter_Note] [bigint] NULL,
	[Non_Billable] [bigint] NOT NULL,
	[Note] [ntext] NULL,
	[Price] [text] NOT NULL,
	[Quantity] [text] NULL,
	[Quantity_In_Hours] [text] NULL,
	[Reference] [text] NULL,
	[Rounded_Quantity] [text] NULL,
	[Rounded_Quantity_In_Hours] [text] NULL,
	[Subject] [bigint] NULL,
	[Timer] [bigint] NULL,
	[Total] [text] NULL,
	[Type] [bigint] NOT NULL,
	[User] [bigint] NULL,
	[Utbms_Expense] [bigint] NULL,
	[Vendor] [bigint] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
	[UserTimesheet_key] [decimal](38, 0) NULL,
 CONSTRAINT [PK_Activities] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ContactNotes](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Author] [bigint] NULL,
	[Contact] [bigint] NULL,
	[Date] [text] NULL,
	[Detail] [text] NULL,
	[Matter] [bigint] NULL,
	[Subject] [text] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_ContactNotes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[MatterNotes](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NULL,
	[Author] [bigint] NULL,
	[Contact] [bigint] NULL,
	[Date] [text] NULL,
	[Detail] [ntext] NULL,
	[Matter] [bigint] NULL,
	[Subject] [ntext] NULL,
	[Clio_Internal_ID] [int] NULL,
	[CompanyID] [uniqueidentifier] NULL,
 CONSTRAINT [PK_MatterNotes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Allocations](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Amount] [text] NOT NULL,
	[Bill] [bigint] NULL,
	[Contact] [bigint] NULL,
	[Date] [text] NULL,
	[Description] [text] NULL,
	[Destination_Bank_Account] [bigint] NULL,
	[Has_Online_Payment] [bigint] NOT NULL,
	[Interest] [bigint] NOT NULL,
	[Matter] [bigint] NULL,
	[Parent] [bigint] NULL,
	[Source_Bank_Account] [bigint] NULL,
	[Voided_At] [text] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Allocations] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[BankTransactions](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Allocation] [bigint] NULL,
	[Amount] [text] NOT NULL,
	[Bank_Account] [bigint] NULL,
	[Bill] [bigint] NULL,
	[Client] [bigint] NULL,
	[Clio_Payments_Transaction] [bigint] NOT NULL,
	[Confirmation] [text] NULL,
	[Currency] [text] NULL,
	[Date] [text] NOT NULL,
	[Description] [text] NULL,
	[Exchange_Rate] [text] NULL,
	[Funds_In] [text] NOT NULL,
	[Funds_Out] [text] NOT NULL,
	[Matter] [bigint] NULL,
	[Source] [text] NULL,
	[Transaction_Type] [text] NULL,
	[Type] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_BankTransactions] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Bills](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Balance] [text] NOT NULL,
	[Bill_Theme] [bigint] NULL,
	[Can_Update] [bigint] NOT NULL,
	[Client] [bigint] NULL,
	[Credits_Issued] [text] NOT NULL,
	[Currency] [bigint] NULL,
	[Destination_Account] [bigint] NULL,
	[Due] [text] NOT NULL,
	[Due_At] [text] NOT NULL,
	[End_At] [text] NULL,
	[Group] [bigint] NULL,
	[Issued_At] [text] NOT NULL,
	[Kind] [bigint] NOT NULL,
	[Memo] [text] NULL,
	[Number] [text] NULL,
	[Original_Bill] [bigint] NULL,
	[Paid] [text] NOT NULL,
	[Paid_At] [text] NULL,
	[Purchase_Order] [text] NULL,
	[Secondary_Tax_Rate] [text] NOT NULL,
	[Secondary_Tax_Sum] [text] NOT NULL,
	[Shared] [bigint] NOT NULL,
	[Start_At] [text] NULL,
	[State] [bigint] NOT NULL,
	[Sub_Total] [text] NOT NULL,
	[Subject] [text] NULL,
	[Tax_Rate] [text] NOT NULL,
	[Tax_Sum] [text] NOT NULL,
	[Total] [text] NOT NULL,
	[Type] [bigint] NOT NULL,
	[User] [bigint] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Bills] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[BillThemes](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Account_Id] [bigint] NOT NULL,
	[Default] [bigint] NOT NULL,
	[Name] [text] NULL,
 CONSTRAINT [PK_BillThemes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[LineItems](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Activity] [bigint] NULL,
	[Bill] [bigint] NULL,
	[Date] [text] NOT NULL,
	[Description] [ntext] NULL,
	[Group_Ordering] [bigint] NOT NULL,
	[Matter] [bigint] NULL,
	[Note] [ntext] NULL,
	[Price] [text] NOT NULL,
	[Quantity] [text] NOT NULL,
	[Secondary_Tax] [text] NULL,
	[Secondary_Taxable] [bigint] NOT NULL,
	[Sub_Total] [text] NOT NULL,
	[Tax] [text] NOT NULL,
	[Taxable] [bigint] NOT NULL,
	[Total] [text] NOT NULL,
	[User] [bigint] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Type] [nvarchar](150) NOT NULL,
	[Kind] [nvarchar](150) NOT NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_LineItems] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Matters](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [ntext] NULL,
	[Redacted] [bigint] NOT NULL,
	[Billable] [bigint] NOT NULL,
	[Billing_Method] [bigint] NOT NULL,
	[Client] [bigint] NULL,
	[Client_Reference] [text] NULL,
	[Close_Date] [text] NULL,
	[Contingency_Fee] [bigint] NULL,
	[Custom_Number] [text] NULL,
	[Description] [text] NULL,
	[Display_Number] [text] NULL,
	[Folder] [bigint] NULL,
	[Group] [bigint] NULL,
	[Location] [text] NULL,
	[Maildrop_Address] [text] NULL,
	[Open_Date] [text] NULL,
	[Originating_Attorney] [bigint] NULL,
	[Pending_Date] [text] NULL,
	[Practice_Area] [bigint] NULL,
	[Responsible_Attorney] [bigint] NULL,
	[Status] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
	[Custom_Rate_Type] [nvarchar](max) NULL,
	[Custom_Rate_Rates] [nvarchar](max) NULL
 CONSTRAINT [PK_Matters] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Tasks](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [ntext] NULL,
	[Redacted] [bigint] NOT NULL,
	[Assignee] [bigint] NULL,
	[Assigner] [bigint] NULL,
	[Completed_At] [text] NULL,
	[Description] [ntext] NULL,
	[Due_At] [text] NULL,
	[Matter] [bigint] NULL,
	[Name] [ntext] NULL,
	[Notify_Completion] [bigint] NOT NULL,
	[Priority] [bigint] NOT NULL,
	[Status] [bigint] NOT NULL,
	[Statute_Of_Limitations] [bigint] NOT NULL,
	[Task_Type] [bigint] NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Tasks] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ActivityDescriptions](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [ntext] NULL,
	[Redacted] [bigint] NOT NULL,
	[Accessible_To_User] [bigint] NOT NULL,
	[Default] [bigint] NOT NULL,
	[Name] [ntext] NULL,
	[Utbms_Activity] [bigint] NULL,
	[Utbms_Activity_Id] [bigint] NULL,
	[Utbms_Task] [bigint] NULL,
	[Utbms_Task_Id] [bigint] NULL,
	[Visible_To_Co_Counsel] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_ActivityDescriptions] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ActivityRates](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Co_Counsel_Contact_Id] [bigint] NULL,
	[Flat_Rate] [bigint] NOT NULL,
	[Group] [bigint] NULL,
	[Rate] [text] NOT NULL,
	[User] [bigint] NULL,
 CONSTRAINT [PK_ActivityRates] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[BankAccounts](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Account_Number] [text] NULL,
	[Balance] [text] NOT NULL,
	[Clio_Payment_Page_Link] [text] NULL,
	[Clio_Payments_Merchant_Account_Mapping] [bigint] NULL,
	[Currency] [text] NULL,
	[Default_Account] [bigint] NOT NULL,
	[Domicile_Branch] [text] NULL,
	[General_Ledger_Number] [text] NULL,
	[Holder] [text] NULL,
	[Institution] [text] NULL,
	[Name] [text] NULL,
	[Swift] [text] NULL,
	[Transit_Number] [text] NULL,
	[Type] [bigint] NOT NULL,
	[User] [bigint] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_BankAccounts] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[BankTransfers](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Amount] [text] NOT NULL,
	[Client] [bigint] NULL,
	[Date] [text] NOT NULL,
	[Description] [text] NULL,
	[Destination_Account] [bigint] NULL,
	[Matter] [bigint] NULL,
	[Source_Account] [bigint] NULL,
 CONSTRAINT [PK_BankTransfers] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[CalendarEntries](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[All_Day] [bigint] NOT NULL,
	[Calendar_Owner] [bigint] NULL,
	[Court_Rule] [bigint] NOT NULL,
	[Description] [text] NULL,
	[End_At] [text] NULL,
	[Location] [nvarchar](2500) NULL,
	[Matter] [bigint] NULL,
	[Matter_Docket] [bigint] NULL,
	[Parent_Calendar_Entry] [bigint] NULL,
	[Parent_Calendar_Entry_Id] [bigint] NULL,
	[Permission] [bigint] NOT NULL,
	[Recurrence_Rule] [text] NULL,
	[Start_At] [text] NULL,
	[Summary] [nvarchar](2500) NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_CalendarEntries] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Calendars](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Color] [bigint] NOT NULL,
	[Court_Rules_Default_Calendar] [bigint] NOT NULL,
	[Creator] [bigint] NULL,
	[Light_Color] [text] NULL,
	[Name] [text] NULL,
	[Permission] [bigint] NOT NULL,
	[Source] [bigint] NOT NULL,
	[Type] [bigint] NOT NULL,
	[Visible] [bigint] NOT NULL,
	[Clio_Internal_ID] [int] NULL,
	[CompanyID] [uniqueidentifier] NULL,
 CONSTRAINT [PK_Calendars] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Communications](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Body] [text] NULL,
	[Date] [text] NULL,
	[Matter] [bigint] NULL,
	[Received_At] [text] NULL,
	[Subject] [text] NULL,
	[Type] [bigint] NOT NULL,
	[User] [bigint] NULL,
 CONSTRAINT [PK_Communications] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ConversationMessages](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Body] [text] NULL,
	[Conversation] [bigint] NULL,
	[Document] [bigint] NULL,
	[Sender] [bigint] NULL,
 CONSTRAINT [PK_ConversationMessages] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Conversations](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[First_Message] [bigint] NULL,
	[Last_Message] [bigint] NULL,
	[Matter] [bigint] NULL,
	[Subject] [text] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Conversations] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[CreditCards](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Account] [bigint] NULL,
	[Card_Type] [bigint] NOT NULL,
	[Contact] [bigint] NULL,
	[Expiry_Month] [text] NULL,
	[Expiry_Year] [text] NULL,
	[Number] [text] NULL,
	[User] [bigint] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_CreditCards] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[CreditMemos](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Amount] [text] NOT NULL,
	[Contact] [bigint] NULL,
	[Date] [text] NULL,
	[Description] [text] NULL,
	[Discount] [bigint] NOT NULL,
	[User] [bigint] NULL,
	[Voided_At] [text] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_CreditMemos] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Currencies](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [text] NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Code] [text] NULL,
	[Sign] [text] NULL,
 CONSTRAINT [PK_Currencies] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[CustomActions](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Label] [text] NULL,
	[Target_Url] [text] NULL,
	[UI_Reference] [bigint] NOT NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_CustomActions] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[CustomFieldSets](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Displayed] [bigint] NOT NULL,
	[Name] [text] NULL,
	[Parent_Type] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_CustomFieldSets] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[DocumentAutomations](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Document_Template] [bigint] NULL,
	[FileName] [text] NULL,
	[Matter] [bigint] NULL,
	[State] [bigint] NOT NULL,
 CONSTRAINT [PK_DocumentAutomations] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[DocumentCategories](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Name] [text] NULL,
 CONSTRAINT [PK_DocumentCategories] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Documents](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Contact] [bigint] NULL,
	[Creator] [bigint] NULL,
	[Deleted_At] [text] NULL,
	[Document_Category] [bigint] NULL,
	[Latest_Document_Version] [bigint] NULL,
	[Locked] [bigint] NOT NULL,
	[Matter] [bigint] NULL,
	[Name] [text] NULL,
	[Parent] [bigint] NULL,
	[Received_At] [text] NULL,
	[Type] [bigint] NOT NULL,
	[CompanyID] [uniqueidentifier] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Documents] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[DocumentTemplates](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Content_Type] [text] NULL,
	[Document_Category] [bigint] NULL,
	[FileName] [text] NULL,
	[Last_Modified_By] [bigint] NULL,
	[Size] [bigint] NOT NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_DocumentTemplates] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ExpenseCategories](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Accessible_To_User] [bigint] NOT NULL,
	[Entry_Type] [bigint] NOT NULL,
	[Name] [text] NULL,
	[Rate] [text] NULL,
	[Utbms_Code] [bigint] NULL,
	[Xero_Expense_Code] [text] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_ExpenseCategories] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Groups](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Name] [text] NULL,
	[Type] [bigint] NOT NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Groups] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Reminders](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Duration] [bigint] NOT NULL,
	[Next_Delivery_At] [text] NULL,
	[Notification_Method] [bigint] NULL,
	[State] [bigint] NOT NULL,
	[Subject] [bigint] NULL,
 CONSTRAINT [PK_Reminders] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TableSettings](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[TableName] [text] NULL,
	[DateLastRefreshedUtc] [text] NOT NULL,
	[EventToken] [text] NULL,
 CONSTRAINT [PK_TableSettings] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TaskCalendars](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Color] [bigint] NOT NULL,
	[Name] [text] NULL,
	[Visible] [bigint] NOT NULL,
 CONSTRAINT [PK_TaskCalendars] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TaskTemplateLists](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Creator] [bigint] NULL,
	[Description] [text] NULL,
	[Name] [text] NULL,
	[Practice_Area] [bigint] NULL,
 CONSTRAINT [PK_TaskTemplateLists] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TaskTemplates](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Description] [text] NULL,
	[Name] [text] NULL,
	[Priority] [bigint] NOT NULL,
	[Private] [bigint] NOT NULL,
	[Task_Template_List] [bigint] NULL,
	[Template_Creator] [bigint] NULL,
 CONSTRAINT [PK_TaskTemplates] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TextSnippets](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Phrase] [text] NULL,
	[Snippet] [text] NULL,
	[Whole_Word] [bigint] NOT NULL,
 CONSTRAINT [PK_TextSnippets] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[Timers](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Elapsed_Time] [text] NULL,
	[Start_Time] [text] NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_Timers] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[UserInvitations](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Account_ID] [bigint] NOT NULL,
	[EMail] [text] NULL,
	[Message] [text] NULL,
	[UID] [bigint] NULL,
	[User_Type] [bigint] NOT NULL,
	[Clio_Internal_ID] [int] NULL,
 CONSTRAINT [PK_UserInvitations] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[UserRoles](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Name] [text] NULL,
 CONSTRAINT [PK_UserRoles] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[UserTypes](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Name] [text] NULL,
 CONSTRAINT [PK_UserTypes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[UtbmsCodes](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Code] [text] NULL,
	[Description] [text] NULL,
	[Name] [text] NULL,
	[Type] [bigint] NOT NULL,
	[Utbms_Set_Id] [bigint] NULL,
 CONSTRAINT [PK_UtbmsCodes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[UtbmsSets](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [varchar](50) NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Enabled] [bigint] NOT NULL,
	[Name] [text] NULL,
 CONSTRAINT [PK_UtbmsSets] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[WebHooks](
	[ID] [bigint] IDENTITY(1,1) NOT NULL,
	[ETag] [text] NULL,
	[Created_At] [text] NULL,
	[Updated_At] [text] NULL,
	[JSON] [text] NULL,
	[Redacted] [bigint] NOT NULL,
	[Expires_At] [text] NULL,
	[Fields] [text] NULL,
	[Model] [bigint] NOT NULL,
	[Shared_Secret] [text] NULL,
	[Status] [bigint] NOT NULL,
	[Url] [text] NULL,
	[User] [bigint] NULL,
 CONSTRAINT [PK_WebHooks] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ActivityTypes](
	[ID] [int] NOT NULL,
	[ActivityType] [nvarchar](150) NOT NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[AppCurrencies](
	[ID] [int] NOT NULL,
	[Sign] [nvarchar](50) NOT NULL,
	[Name] [nvarchar](150) NOT NULL,
	[RateToUSD] [float] NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[BillKinds](
	[ID] [int] NOT NULL,
	[KindName] [nvarchar](150) NOT NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[BillStateTypes](
	[ID] [int] NOT NULL,
	[StatusType] [nvarchar](150) NOT NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[BillTypeNames](
	[ID] [int] NOT NULL,
	[BillTypeName] [nvarchar](150) NOT NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[BillTypes](
	[ID] [int] NOT NULL,
	[BillType] [nvarchar](150) NOT NULL,
 CONSTRAINT [PK_BillTypes] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY];

CREATE TABLE [dbo].[MatterStatusTypes](
	[ID] [int] NOT NULL,
	[StatusName] [nvarchar](150) NOT NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[BillingMethods](
	[ID] [int] NOT NULL,
	[MethodName] [nvarchar](150) NOT NULL
) ON [PRIMARY];

CREATE TABLE [dbo].[_EKPCompanies](
	[ID] [uniqueidentifier] NOT NULL,
	[CompanyName] [nvarchar](150) NOT NULL,
	[BillingOffice] [nvarchar](650) NOT NULL,
	[BillingCurrency] [nvarchar](150) NOT NULL,
	[ClioApiKey] [nvarchar](650) NULL,
	[ClioApiSecret] [nvarchar](650) NULL,
	[ClioToken] [nvarchar](max) NULL,
	[ClioRefreshToken] [nvarchar](max) NULL,
	[ClioAPIURL] [nvarchar](650) NULL,
	[Active] [bit] NOT NULL,
	[LastRefreshTime] [datetime] NULL,
	[UserName] [nvarchar](150) NULL,
	[Password] [nvarchar](150) NULL,
	[LastTokenRefreshTime] [datetime] NULL,
	[MainCurrencyID] [int] NULL,
	[LastSyncTime] [datetime] NULL,
	[FullSyncStart] [datetime] NULL,
	[FullSyncStop] [datetime] NULL,
 CONSTRAINT [PK__EKPCompanies] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[ErrorInfo](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[Type] [nvarchar](250) NOT NULL,
	[Message] [nvarchar](max) NOT NULL,
	[StackTrace] [nvarchar](max) NULL,
	[InnerException] [nvarchar](max) NULL,
	[ExtraInfo] [nvarchar](max) NULL,
	[DateCreated] [datetime] NOT NULL,
 CONSTRAINT [PK_ErrorInfo] PRIMARY KEY CLUSTERED 
(
	[ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

CREATE TABLE [dbo].[TempTables](
	[ID] [int] NOT NULL,
	[LastDateUpdated] [datetime] NOT NULL
) ON [PRIMARY];
EXEC(N''CREATE SCHEMA control AUTHORIZATION dbo;'');
CREATE TABLE control.TableInventory
(TableName sysname NOT NULL PRIMARY KEY, Category varchar(30) NOT NULL);
INSERT control.TableInventory(TableName,Category) VALUES
(N''Contacts'',N''CANDIDATE_RAW''),
(N''CustomFields'',N''CANDIDATE_RAW''),
(N''Folders'',N''CANDIDATE_RAW''),
(N''PracticeAreas'',N''CANDIDATE_RAW''),
(N''Relationships'',N''CANDIDATE_RAW''),
(N''TaskTypes'',N''CANDIDATE_RAW''),
(N''Users'',N''CANDIDATE_RAW''),
(N''Activities'',N''CANDIDATE_RAW''),
(N''ContactNotes'',N''CANDIDATE_RAW''),
(N''MatterNotes'',N''CANDIDATE_RAW''),
(N''Allocations'',N''CANDIDATE_RAW''),
(N''BankTransactions'',N''CANDIDATE_RAW''),
(N''Bills'',N''CANDIDATE_RAW''),
(N''BillThemes'',N''CANDIDATE_RAW''),
(N''LineItems'',N''CANDIDATE_RAW''),
(N''Matters'',N''CANDIDATE_RAW''),
(N''Tasks'',N''CANDIDATE_RAW''),
(N''ActivityDescriptions'',N''CANDIDATE_RAW''),
(N''ActivityRates'',N''CANDIDATE_RAW''),
(N''BankAccounts'',N''CANDIDATE_RAW''),
(N''BankTransfers'',N''CANDIDATE_RAW''),
(N''CalendarEntries'',N''CANDIDATE_RAW''),
(N''Calendars'',N''CANDIDATE_RAW''),
(N''Communications'',N''CANDIDATE_RAW''),
(N''ConversationMessages'',N''CANDIDATE_RAW''),
(N''Conversations'',N''CANDIDATE_RAW''),
(N''CreditCards'',N''CANDIDATE_RAW''),
(N''CreditMemos'',N''CANDIDATE_RAW''),
(N''Currencies'',N''CANDIDATE_RAW''),
(N''CustomActions'',N''CANDIDATE_RAW''),
(N''CustomFieldSets'',N''CANDIDATE_RAW''),
(N''DocumentAutomations'',N''CANDIDATE_RAW''),
(N''DocumentCategories'',N''CANDIDATE_RAW''),
(N''Documents'',N''CANDIDATE_RAW''),
(N''DocumentTemplates'',N''CANDIDATE_RAW''),
(N''ExpenseCategories'',N''CANDIDATE_RAW''),
(N''Groups'',N''CANDIDATE_RAW''),
(N''Reminders'',N''CANDIDATE_RAW''),
(N''TableSettings'',N''SUPPORT''),
(N''TaskCalendars'',N''CANDIDATE_RAW''),
(N''TaskTemplateLists'',N''CANDIDATE_RAW''),
(N''TaskTemplates'',N''CANDIDATE_RAW''),
(N''TextSnippets'',N''CANDIDATE_RAW''),
(N''Timers'',N''CANDIDATE_RAW''),
(N''UserInvitations'',N''CANDIDATE_RAW''),
(N''UserRoles'',N''CANDIDATE_RAW''),
(N''UserTypes'',N''CANDIDATE_RAW''),
(N''UtbmsCodes'',N''CANDIDATE_RAW''),
(N''UtbmsSets'',N''CANDIDATE_RAW''),
(N''WebHooks'',N''CANDIDATE_RAW''),
(N''ActivityTypes'',N''LOOKUP''),
(N''AppCurrencies'',N''LOOKUP''),
(N''BillKinds'',N''LOOKUP''),
(N''BillStateTypes'',N''LOOKUP''),
(N''BillTypeNames'',N''LOOKUP''),
(N''BillTypes'',N''LOOKUP''),
(N''MatterStatusTypes'',N''LOOKUP''),
(N''BillingMethods'',N''LOOKUP''),
(N''_EKPCompanies'',N''CONFIG''),
(N''ErrorInfo'',N''SUPPORT''),
(N''TempTables'',N''SUPPORT'');
SELECT t.name COLLATE DATABASE_DEFAULT AS TableName,
 c.name COLLATE DATABASE_DEFAULT AS ColumnName,
 ty.name COLLATE DATABASE_DEFAULT AS TypeName,c.max_length,c.precision,c.scale,
 c.is_nullable,c.is_identity,c.is_computed,c.collation_name COLLATE DATABASE_DEFAULT AS CollationName
INTO #LiveColumns
FROM EKPBI_New_Dev.sys.tables t JOIN EKPBI_New_Dev.sys.schemas s ON s.schema_id=t.schema_id
JOIN EKPBI_New_Dev.sys.columns c ON c.object_id=t.object_id
JOIN EKPBI_New_Dev.sys.types ty ON ty.user_type_id=c.user_type_id
JOIN control.TableInventory i ON i.TableName=t.name COLLATE DATABASE_DEFAULT
WHERE s.name=N''dbo'' ;
SELECT t.name COLLATE DATABASE_DEFAULT AS TableName,
 c.name COLLATE DATABASE_DEFAULT AS ColumnName,
 ty.name COLLATE DATABASE_DEFAULT AS TypeName,c.max_length,c.precision,c.scale,
 c.is_nullable,c.is_identity,c.is_computed,c.collation_name COLLATE DATABASE_DEFAULT AS CollationName
INTO #StageColumns
FROM Clio_Staging.sys.tables t JOIN Clio_Staging.sys.schemas s ON s.schema_id=t.schema_id
JOIN Clio_Staging.sys.columns c ON c.object_id=t.object_id
JOIN Clio_Staging.sys.types ty ON ty.user_type_id=c.user_type_id
JOIN control.TableInventory i ON i.TableName=t.name COLLATE DATABASE_DEFAULT
WHERE s.name=N''dbo'' ;
IF EXISTS(SELECT * FROM #LiveColumns EXCEPT SELECT * FROM #StageColumns)
OR EXISTS(SELECT * FROM #StageColumns EXCEPT SELECT * FROM #LiveColumns)
BEGIN
 SELECT N''LIVE_ONLY_OR_CHANGED'' AS Difference,* FROM
 (SELECT * FROM #LiveColumns EXCEPT SELECT * FROM #StageColumns) d;
 SELECT N''STAGING_ONLY_OR_CHANGED'' AS Difference,* FROM
 (SELECT * FROM #StageColumns EXCEPT SELECT * FROM #LiveColumns) d;
 THROW 51001,''Live schema differs from the September 27 baseline. Setup rolled back. Send the differences before proceeding.'',1;
END;
INSERT dbo._EKPCompanies
 (ID,CompanyName,BillingOffice,BillingCurrency,ClioAPIURL,Active,MainCurrencyID)
SELECT ID,CompanyName,BillingOffice,BillingCurrency,ClioAPIURL,0,MainCurrencyID
FROM EKPBI_New_Dev.dbo._EKPCompanies;
/* Auth fields and all sync timestamps remain NULL; every company remains inactive. */
INSERT dbo.[ActivityTypes] ([ID], [ActivityType]) SELECT [ID], [ActivityType] FROM EKPBI_New_Dev.dbo.[ActivityTypes];
INSERT dbo.[AppCurrencies] ([ID], [Sign], [Name], [RateToUSD]) SELECT [ID], [Sign], [Name], [RateToUSD] FROM EKPBI_New_Dev.dbo.[AppCurrencies];
INSERT dbo.[BillKinds] ([ID], [KindName]) SELECT [ID], [KindName] FROM EKPBI_New_Dev.dbo.[BillKinds];
INSERT dbo.[BillStateTypes] ([ID], [StatusType]) SELECT [ID], [StatusType] FROM EKPBI_New_Dev.dbo.[BillStateTypes];
INSERT dbo.[BillTypeNames] ([ID], [BillTypeName]) SELECT [ID], [BillTypeName] FROM EKPBI_New_Dev.dbo.[BillTypeNames];
INSERT dbo.[BillTypes] ([ID], [BillType]) SELECT [ID], [BillType] FROM EKPBI_New_Dev.dbo.[BillTypes];
INSERT dbo.[MatterStatusTypes] ([ID], [StatusName]) SELECT [ID], [StatusName] FROM EKPBI_New_Dev.dbo.[MatterStatusTypes];
INSERT dbo.[BillingMethods] ([ID], [MethodName]) SELECT [ID], [MethodName] FROM EKPBI_New_Dev.dbo.[BillingMethods];
INSERT dbo.TempTables(ID,LastDateUpdated) VALUES(1,CONVERT(datetime,''19000101'',112));
EXEC sys.sp_executesql N''CREATE PROCEDURE [dbo].[SP_ClearAllData]
 
AS
BEGIN
	-- SET NOCOUNT ON added to prevent extra result sets Table
	-- interfering with SELECT statements.
	SET NOCOUNT ON;


TRUNCATE Table [dbo].[Bills]  

TRUNCATE Table [dbo].[Allocations]  

TRUNCATE Table [dbo].[Activities]  

TRUNCATE Table [dbo].[ActivityDescriptions]  

TRUNCATE Table [dbo].[Matters]  

TRUNCATE Table  [dbo].[Contacts]  

TRUNCATE Table [dbo].[Users]  

TRUNCATE Table [dbo].[PracticeAreas]  

TRUNCATE Table [dbo].[CustomFields]  

TRUNCATE Table [dbo].[CustomFieldSets]  

TRUNCATE Table [dbo].[LineItems]  

TRUNCATE Table [dbo].[Folders]

TRUNCATE Table [dbo].[BankTransactions]

TRUNCATE Table [dbo].[Calendars]

TRUNCATE Table [dbo].[CalendarEntries]

TRUNCATE Table [dbo].[Relationships]

TRUNCATE Table [dbo].[Tasks]

TRUNCATE Table [dbo].[TaskTypes]

TRUNCATE Table [dbo].[MatterNotes]

	UPDATE [dbo].[_EKPCompanies]
   SET [LastRefreshTime] = NULL,[LastSyncTime] = NULL

END'';
EXEC sys.sp_executesql N''CREATE PROCEDURE [dbo].[SP_ClearData]
 @companyID as uniqueidentifier
AS
BEGIN
	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;

DELETE FROM [dbo].[Bills] where CompanyID = @companyID;
 
DELETE FROM [dbo].[Allocations] where CompanyID = @companyID;

DELETE FROM [dbo].[Activities] where CompanyID = @companyID;

DELETE FROM [dbo].[ActivityDescriptions] where CompanyID = @companyID;
 
DELETE FROM [dbo].[Matters] where CompanyID = @companyID;

DELETE FROM  [dbo].[Contacts] where CompanyID = @companyID;

DELETE FROM [dbo].[Users] where CompanyID = @companyID;

DELETE FROM [dbo].[PracticeAreas] where CompanyID = @companyID;

DELETE FROM [dbo].[CustomFields] where CompanyID = @companyID;

DELETE FROM [dbo].[CustomFieldSets] where CompanyID = @companyID;

DELETE FROM [dbo].[LineItems] where CompanyID = @companyID;

DELETE FROM [dbo].[Folders] where CompanyID = @companyID;

Delete FROM [dbo].[MatterNotes] where CompanyID = @companyID;

Delete FROM [dbo].[Tasks] where CompanyID = @companyID;

END'';
EXEC sys.sp_executesql N''CREATE PROCEDURE [dbo].[SP_CleanData]
 
AS
BEGIN
	SET NOCOUNT ON;
	
  
	 
	----------------------------
	UPDATE [dbo].[TempTables]
		   SET [LastDateUpdated] = GETDATE()
		  WHERE ID = 1;

		  ----------------------------
----------------------------

END'';
EXEC sys.sp_executesql N''CREATE PROCEDURE [dbo].[SP_SyncAllData]
 
AS
BEGIN
	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;
UPDATE [dbo].[_EKPCompanies]
   SET [LastRefreshTime] = NULL
 
END'';
EXEC sys.sp_executesql N''CREATE PROCEDURE [dbo].[Stop_Sync]
 
AS
BEGIN
	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;

   UPDATE [dbo].[_EKPCompanies]
   SET  [Active] = 0
 
END'';
EXEC sys.sp_executesql N''CREATE PROCEDURE dbo.SP_CustomExecAfterSync AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 BEGIN TRY
  UPDATE B SET B.Currency=C.MainCurrencyID
  FROM dbo.Bills B JOIN dbo._EKPCompanies C ON C.ID=B.CompanyID
  WHERE B.Currency=0;
 END TRY
 BEGIN CATCH
  INSERT dbo.ErrorInfo([Type],[Message],[ExtraInfo],[DateCreated])
  VALUES(N''''SQL'''',ERROR_MESSAGE(),N''''SP_CustomExecAfterSync'''',GETDATE());
  THROW;
 END CATCH;
END;'';
EXEC sys.sp_executesql N''CREATE PROCEDURE dbo.PopulateTables
 @updateAll bit=0,@specificViewName varchar(250)=NULL,@EverySync bit=0
AS
BEGIN
 THROW 51002,''''Extraction-only staging: disable the reporting hook in the test EXE. No production publish is installed.'''',1;
END;'';
CREATE TABLE control.BootstrapInfo
(ID int NOT NULL PRIMARY KEY CHECK(ID=1), InstalledAtUTC datetime2 NOT NULL,
 BaselineDate date NOT NULL, PublishEnabled bit NOT NULL CHECK(PublishEnabled=0),
 Status nvarchar(100) NOT NULL);
INSERT control.BootstrapInfo VALUES
(1,SYSUTCDATETIME(),''20260927'',0,N''SCHEMA_READY_AUTH_AND_EXE_REVIEW_REQUIRED'');
/* Audit contract only. Biren must implement run transitions and completeness evidence.
   No procedure in this package marks a run VALIDATED, READY or publishes production. */
CREATE TABLE control.SyncRun
(RunID uniqueidentifier NOT NULL PRIMARY KEY,
 StartedAtUTC datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),CompletedAtUTC datetime2 NULL,
 Status varchar(30) NOT NULL CHECK(Status IN (''RUNNING'',''EXTRACTED'',''FAILED'',''ABANDONED'')),
 Mode varchar(10) NOT NULL CHECK(Mode=''FULL''),ErrorMessage nvarchar(max) NULL);
CREATE UNIQUE INDEX UX_SyncRun_OneRunning ON control.SyncRun(Status) WHERE Status=''RUNNING'';
CREATE TABLE control.SyncEndpointResult
(RunID uniqueidentifier NOT NULL REFERENCES control.SyncRun(RunID),
 CompanyID uniqueidentifier NOT NULL,Endpoint nvarchar(150) NOT NULL,
 Status varchar(20) NOT NULL CHECK(Status IN (''PENDING'',''RUNNING'',''COMPLETE'',''FAILED'')),
 PagesReceived int NOT NULL DEFAULT 0 CHECK(PagesReceived>=0),
 RowsReceived bigint NOT NULL DEFAULT 0 CHECK(RowsReceived>=0),
 RowsWritten bigint NOT NULL DEFAULT 0 CHECK(RowsWritten>=0),
 PaginationComplete bit NOT NULL DEFAULT 0,
 StartedAtUTC datetime2 NULL,CompletedAtUTC datetime2 NULL,ErrorMessage nvarchar(max) NULL,
 PRIMARY KEY(RunID,CompanyID,Endpoint));
CREATE ROLE clio_stage_loader AUTHORIZATION dbo;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Contacts] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[CustomFields] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Folders] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[PracticeAreas] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Relationships] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[TaskTypes] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Users] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Activities] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[ContactNotes] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[MatterNotes] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Allocations] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[BankTransactions] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Bills] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[BillThemes] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[LineItems] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Matters] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Tasks] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[ActivityDescriptions] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[ActivityRates] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[BankAccounts] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[BankTransfers] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[CalendarEntries] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Calendars] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Communications] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[ConversationMessages] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Conversations] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[CreditCards] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[CreditMemos] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Currencies] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[CustomActions] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[CustomFieldSets] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[DocumentAutomations] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[DocumentCategories] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Documents] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[DocumentTemplates] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[ExpenseCategories] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Groups] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Reminders] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[TableSettings] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[TaskCalendars] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[TaskTemplateLists] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[TaskTemplates] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[TextSnippets] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[Timers] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[UserInvitations] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[UserRoles] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[UserTypes] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[UtbmsCodes] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[UtbmsSets] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON OBJECT::dbo.[WebHooks] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[ActivityTypes] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[AppCurrencies] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[BillKinds] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[BillStateTypes] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[BillTypeNames] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[BillTypes] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[MatterStatusTypes] TO clio_stage_loader;
GRANT SELECT ON OBJECT::dbo.[BillingMethods] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[_EKPCompanies] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[ErrorInfo] TO clio_stage_loader;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.[TempTables] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[SP_ClearAllData] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[SP_ClearData] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[SP_CleanData] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[SP_SyncAllData] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[Stop_Sync] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[SP_CustomExecAfterSync] TO clio_stage_loader;
GRANT EXECUTE ON OBJECT::dbo.[PopulateTables] TO clio_stage_loader;
GRANT SELECT,INSERT,UPDATE ON OBJECT::control.SyncRun TO clio_stage_loader;
GRANT SELECT,INSERT,UPDATE ON OBJECT::control.SyncEndpointResult TO clio_stage_loader;
GRANT SELECT ON OBJECT::control.TableInventory TO clio_stage_loader;
GRANT SELECT ON OBJECT::control.BootstrapInfo TO clio_stage_loader;
COMMIT TRANSACTION;
SELECT * FROM control.BootstrapInfo;
SELECT Category,COUNT(*) AS TableCount FROM control.TableInventory GROUP BY Category;
SELECT ID,CompanyName,Active FROM dbo._EKPCompanies;
END TRY
BEGIN CATCH
 IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
';
EXEC sys.sp_executesql @setup;
