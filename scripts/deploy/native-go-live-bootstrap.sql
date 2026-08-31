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
        N'FluxKnowledgeNativeGoLiveDrop',
        N'FluxKnowledgeNativeGoLiveManageAppPool',
        N'FluxKnowledgeNativeGoLiveObserveAppPool'))
    THROW 51000, 'native-go-live-bootstrap-procedure-already-exists', 1;
GO

IF SUSER_ID(N'IIS AppPool\FluxKnowledge') IS NULL
    CREATE LOGIN [IIS AppPool\FluxKnowledge] FROM WINDOWS;
ALTER SERVER ROLE [sysadmin] ADD MEMBER [IIS AppPool\FluxKnowledge];
IF IS_SRVROLEMEMBER(N'sysadmin', N'IIS AppPool\FluxKnowledge')<>1
    THROW 51000, 'native-go-live-app-pool-sysadmin-not-proved', 1;
GO

-- BEGIN HASHED PROCEDURE: FluxKnowledgeNativeGoLiveCreate
CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate
    @Catalogue nvarchar(128),
    @DataFile nvarchar(260),
    @LogFile nvarchar(260),
    @AppPoolLogin nvarchar(128),
    @AppPoolSid varbinary(85)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Catalogue COLLATE Latin1_General_100_BIN2 <> N'FluxKnowledge' OR
       @DataFile COLLATE Latin1_General_100_BIN2 <> N'I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf' OR
       @LogFile COLLATE Latin1_General_100_BIN2 <> N'I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf' OR
       @AppPoolLogin COLLATE Latin1_General_100_BIN2 <> N'IIS AppPool\FluxKnowledge' OR
       @AppPoolSid IS NULL OR DATALENGTH(@AppPoolSid) NOT BETWEEN 8 AND 85
        THROW 51000, 'native-go-live-create-identity-refused', 1;
    IF DB_ID(N'FluxKnowledge') IS NOT NULL
        THROW 51000, 'native-go-live-create-catalogue-exists', 1;
    IF SUSER_SID(N'IIS AppPool\FluxKnowledge') <> @AppPoolSid
        THROW 51000, 'native-go-live-create-app-pool-sid-mismatch', 1;

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

-- BEGIN HASHED PROCEDURE: FluxKnowledgeNativeGoLiveManageAppPool
CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveManageAppPool
    @Catalogue nvarchar(128),
    @AppPoolLogin nvarchar(128),
    @AppPoolSid varbinary(85),
    @BootstrapLogin nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Catalogue COLLATE Latin1_General_100_BIN2 <> N'FluxKnowledge' OR
       @AppPoolLogin COLLATE Latin1_General_100_BIN2 <> N'IIS AppPool\FluxKnowledge' OR
       @AppPoolSid IS NULL OR DATALENGTH(@AppPoolSid) NOT BETWEEN 8 AND 85 OR
       @BootstrapLogin COLLATE Latin1_General_100_BIN2 <> ORIGINAL_LOGIN() COLLATE Latin1_General_100_BIN2 OR
       DB_ID(N'FluxKnowledge') IS NULL
        THROW 51000, 'native-go-live-app-pool-identity-refused', 1;
    IF SUSER_SID(N'IIS AppPool\FluxKnowledge') <> @AppPoolSid
        THROW 51000, 'native-go-live-app-pool-sid-mismatch', 1;

    ALTER SERVER ROLE [sysadmin] ADD MEMBER [IIS AppPool\FluxKnowledge];
    ALTER AUTHORIZATION ON DATABASE::[FluxKnowledge] TO [IIS AppPool\FluxKnowledge];

    IF IS_SRVROLEMEMBER(N'sysadmin', N'IIS AppPool\FluxKnowledge')<>1 OR
       EXISTS (SELECT 1 FROM sys.databases
               WHERE name=N'FluxKnowledge' AND owner_sid<>SUSER_SID(N'IIS AppPool\FluxKnowledge'))
        THROW 51000, 'native-go-live-app-pool-authority-not-proved', 1;
END;
GO
-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveManageAppPool

-- BEGIN HASHED PROCEDURE: FluxKnowledgeNativeGoLiveObserveAppPool
CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveObserveAppPool
    @Catalogue nvarchar(128),
    @AppPoolLogin nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Catalogue COLLATE Latin1_General_100_BIN2 <> N'FluxKnowledge' OR
       @AppPoolLogin COLLATE Latin1_General_100_BIN2 <> N'IIS AppPool\FluxKnowledge' OR
       DB_ID(N'FluxKnowledge') IS NULL OR SUSER_ID(N'IIS AppPool\FluxKnowledge') IS NULL
        THROW 51000, 'native-go-live-observe-identity-refused', 1;

    EXEC(N'
        EXECUTE AS LOGIN = N''IIS AppPool\FluxKnowledge'';
        BEGIN TRY
            SELECT SUSER_SID(),
                   CONVERT(int,COALESCE(IS_SRVROLEMEMBER(N''sysadmin''),0)),
                   CONVERT(int,CASE WHEN HAS_PERMS_BY_NAME(NULL,N''SERVER'',N''CONNECT SQL'')=1
                                        AND HAS_PERMS_BY_NAME(N''FluxKnowledge'',N''DATABASE'',N''CONNECT'')=1
                                    THEN 1 ELSE 0 END);
            REVERT;
        END TRY
        BEGIN CATCH
            IF ORIGINAL_LOGIN()<>SUSER_SNAME() REVERT;
            THROW;
        END CATCH;');
END;
GO
-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveObserveAppPool

DECLARE @BootstrapLogin sysname = N'$(NativeGoLiveBootstrapLogin)';
IF @BootstrapLogin=N'__SUPPLY_AT_EXECUTION__' OR SUSER_ID(@BootstrapLogin) IS NULL
    THROW 51000, 'native-go-live-bootstrap-login-missing', 1;
GO
