:On Error exit
-- Reviewed SQL Server bootstrap authority for the native go-live lifecycle.
-- Invoke with sqlcmd -b as a trusted local administrator, supplying NativeGoLiveBootstrapLogin.
:setvar NativeGoLiveBootstrapLogin "__SUPPLY_AT_EXECUTION__"

USE master;
GO

IF EXISTS (
    SELECT 1
    FROM sys.procedures procedure_object
    JOIN sys.schemas procedure_schema ON procedure_schema.schema_id=procedure_object.schema_id
    WHERE procedure_schema.name=N'dbo' AND procedure_object.name IN (
        N'FluxKnowledgeNativeGoLiveCreate',
        N'FluxKnowledgeNativeGoLiveDrop'))
    THROW 51000, 'native-go-live-bootstrap-procedure-already-exists', 1;
GO

-- BEGIN HASHED PROCEDURE: FluxKnowledgeNativeGoLiveCreate
CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate
    @Catalogue nvarchar(128),
    @DataFile nvarchar(260),
    @LogFile nvarchar(260)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Catalogue COLLATE Latin1_General_100_BIN2 <> N'FluxKnowledge' OR
       @DataFile COLLATE Latin1_General_100_BIN2 <> N'I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf' OR
       @LogFile COLLATE Latin1_General_100_BIN2 <> N'I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf'
        THROW 51000, 'native-go-live-create-identity-refused', 1;
    IF DB_ID(N'FluxKnowledge') IS NOT NULL
        THROW 51000, 'native-go-live-create-catalogue-exists', 1;

    DECLARE @CreateDatabase nvarchar(max) =
        N'CREATE DATABASE [FluxKnowledge] ON PRIMARY ' +
        N'(NAME=N''FluxKnowledge'',FILENAME=N''' + REPLACE(@DataFile, N'''', N'''''') + N''') ' +
        N'LOG ON (NAME=N''FluxKnowledge_log'',FILENAME=N''' + REPLACE(@LogFile, N'''', N'''''') + N''');';
    EXEC sys.sp_executesql @CreateDatabase;

END;
GO
-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveCreate

-- BEGIN HASHED PROCEDURE: FluxKnowledgeNativeGoLiveDrop
CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveDrop
    @Catalogue nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Catalogue COLLATE Latin1_General_100_BIN2 <> N'FluxKnowledge'
        THROW 51000, 'native-go-live-drop-identity-refused', 1;
    IF DB_ID(N'FluxKnowledge') IS NULL RETURN;

    ALTER DATABASE [FluxKnowledge] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [FluxKnowledge];
END;
GO
-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveDrop

DECLARE @BootstrapLogin sysname = N'$(NativeGoLiveBootstrapLogin)';
IF @BootstrapLogin=N'__SUPPLY_AT_EXECUTION__' OR SUSER_ID(@BootstrapLogin) IS NULL
    THROW 51000, 'native-go-live-bootstrap-login-missing', 1;
GO
