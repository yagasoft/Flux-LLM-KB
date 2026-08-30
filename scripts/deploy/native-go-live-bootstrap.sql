:On Error exit
-- Reviewed SQL Server bootstrap authority for the native go-live lifecycle.
-- Invoke with sqlcmd -b as a sysadmin, supplying NativeGoLiveBootstrapLogin.
-- This source creates a fresh local signing certificate; it contains no exported key or connection material.
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

-- BEGIN HASHED SECURITY BOOTSTRAP
IF CERT_ID(N'FluxKnowledgeNativeGoLiveCertificate') IS NOT NULL OR
   SUSER_ID(N'FluxKnowledgeNativeGoLiveCertificateLogin') IS NOT NULL OR
   EXISTS (
       SELECT 1
       FROM sys.crypt_properties property
       JOIN sys.objects object_value ON object_value.object_id=property.major_id
       WHERE property.class_desc=N'OBJECT_OR_COLUMN' AND
             SCHEMA_NAME(object_value.schema_id)=N'dbo' AND
             object_value.name IN (
                 N'FluxKnowledgeNativeGoLiveCreate',
                 N'FluxKnowledgeNativeGoLiveDrop',
                 N'FluxKnowledgeNativeGoLiveManageAppPool',
                 N'FluxKnowledgeNativeGoLiveObserveAppPool'))
    THROW 51000, 'native-go-live-bootstrap-security-artifact-exists', 1;

CREATE CERTIFICATE FluxKnowledgeNativeGoLiveCertificate
    WITH SUBJECT = N'FluxKnowledge native go-live constrained module authority';

CREATE LOGIN FluxKnowledgeNativeGoLiveCertificateLogin
    FROM CERTIFICATE FluxKnowledgeNativeGoLiveCertificate;

IF CERT_ID(N'FluxKnowledgeNativeGoLiveCertificate') IS NULL OR
   SUSER_ID(N'FluxKnowledgeNativeGoLiveCertificateLogin') IS NULL
    THROW 51000, 'native-go-live-bootstrap-security-artifact-creation-not-proved', 1;

DECLARE @SigningCertificateThumbprint varbinary(85) =
    (SELECT thumbprint FROM sys.certificates WHERE name=N'FluxKnowledgeNativeGoLiveCertificate');
DECLARE @SigningCertificateLoginSid varbinary(85) =
    SUSER_SID(N'FluxKnowledgeNativeGoLiveCertificateLogin');
IF @SigningCertificateThumbprint IS NULL OR @SigningCertificateLoginSid IS NULL OR
   @SigningCertificateLoginSid <> @SigningCertificateThumbprint
    THROW 51000, 'native-go-live-bootstrap-certificate-login-mismatch', 1;
-- END HASHED SECURITY BOOTSTRAP
GO

GRANT CREATE ANY DATABASE TO FluxKnowledgeNativeGoLiveCertificateLogin;
GRANT ALTER ANY DATABASE TO FluxKnowledgeNativeGoLiveCertificateLogin;
GRANT ALTER ANY LOGIN TO FluxKnowledgeNativeGoLiveCertificateLogin;
GRANT IMPERSONATE ANY LOGIN TO FluxKnowledgeNativeGoLiveCertificateLogin;
GRANT VIEW SERVER STATE TO FluxKnowledgeNativeGoLiveCertificateLogin;
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

    IF SUSER_ID(N'IIS AppPool\FluxKnowledge') IS NULL
        CREATE LOGIN [IIS AppPool\FluxKnowledge] FROM WINDOWS;
    IF SUSER_SID(N'IIS AppPool\FluxKnowledge') <> @AppPoolSid
        THROW 51000, 'native-go-live-create-app-pool-sid-mismatch', 1;

    GRANT CONNECT SQL TO [IIS AppPool\FluxKnowledge];

    DECLARE @CreateDatabase nvarchar(max) =
        N'CREATE DATABASE [FluxKnowledge] ON PRIMARY ' +
        N'(NAME=N''FluxKnowledge'',FILENAME=N''' + REPLACE(@DataFile, N'''', N'''''') + N''') ' +
        N'LOG ON (NAME=N''FluxKnowledge_log'',FILENAME=N''' + REPLACE(@LogFile, N'''', N'''''') + N''');';
    EXEC sys.sp_executesql @CreateDatabase;

    EXEC(N'USE [FluxKnowledge];
        CREATE USER [IIS AppPool\FluxKnowledge] FOR LOGIN [IIS AppPool\FluxKnowledge];
        GRANT CONNECT TO [IIS AppPool\FluxKnowledge];
        ALTER ROLE [db_datareader] ADD MEMBER [IIS AppPool\FluxKnowledge];
        ALTER ROLE [db_datawriter] ADD MEMBER [IIS AppPool\FluxKnowledge];');
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

    IF SUSER_ID(N'IIS AppPool\FluxKnowledge') IS NULL
        CREATE LOGIN [IIS AppPool\FluxKnowledge] FROM WINDOWS;
    IF SUSER_SID(N'IIS AppPool\FluxKnowledge') <> @AppPoolSid
        THROW 51000, 'native-go-live-app-pool-sid-mismatch', 1;

    GRANT CONNECT SQL TO [IIS AppPool\FluxKnowledge];
    EXEC(N'USE [FluxKnowledge];
        IF DATABASE_PRINCIPAL_ID(N''IIS AppPool\FluxKnowledge'') IS NULL
            CREATE USER [IIS AppPool\FluxKnowledge] FOR LOGIN [IIS AppPool\FluxKnowledge];
        GRANT CONNECT TO [IIS AppPool\FluxKnowledge];
        IF EXISTS (
            SELECT 1 FROM sys.database_role_members rm
            JOIN sys.database_principals role_principal ON role_principal.principal_id=rm.role_principal_id
            WHERE rm.member_principal_id=DATABASE_PRINCIPAL_ID(N''IIS AppPool\FluxKnowledge'')
              AND role_principal.name NOT IN (N''db_datareader'',N''db_datawriter''))
            THROW 51000, ''native-go-live-app-pool-role-refused'', 1;
        ALTER ROLE [db_datareader] ADD MEMBER [IIS AppPool\FluxKnowledge];
        ALTER ROLE [db_datawriter] ADD MEMBER [IIS AppPool\FluxKnowledge];');

    ALTER AUTHORIZATION ON DATABASE::[FluxKnowledge]
        TO FluxKnowledgeNativeGoLiveCertificateLogin;
    EXEC(N'USE [FluxKnowledge];
        IF DATABASE_PRINCIPAL_ID(N''' + REPLACE(@BootstrapLogin, N'''', N'''''') + N''') IS NOT NULL
            DROP USER ' + QUOTENAME(@BootstrapLogin) + N';
        REVOKE CONNECT FROM public;
        REVOKE EXECUTE FROM public;');

    IF EXISTS (SELECT 1 FROM sys.databases
               WHERE name=N'FluxKnowledge' AND owner_sid<>SUSER_SID(N'FluxKnowledgeNativeGoLiveCertificateLogin'))
        THROW 51000, 'native-go-live-catalogue-owner-transfer-not-proved', 1;
    EXEC(N'REVOKE EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveCreate FROM '+QUOTENAME(@BootstrapLogin)+N';');
    EXEC(N'REVOKE EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveDrop FROM '+QUOTENAME(@BootstrapLogin)+N';');
    EXEC(N'REVOKE EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveManageAppPool FROM '+QUOTENAME(@BootstrapLogin)+N';');
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

    DECLARE @BootstrapLogin sysname = ORIGINAL_LOGIN();
    IF @BootstrapLogin IS NULL OR @BootstrapLogin=N''
        THROW 51000, 'native-go-live-observe-bootstrap-login-missing', 1;

    BEGIN TRY
    EXEC(N'
        EXECUTE AS LOGIN = N''IIS AppPool\FluxKnowledge'';
        BEGIN TRY
            SELECT SUSER_SID(),
                   (SELECT sid FROM [FluxKnowledge].sys.database_principals
                    WHERE name=N''IIS AppPool\FluxKnowledge''),
                   CONVERT(int,COALESCE(IS_SRVROLEMEMBER(N''sysadmin''),0)),
                   CONVERT(int,CASE WHEN HAS_PERMS_BY_NAME(NULL,N''SERVER'',N''CONNECT SQL'')=1
                                        AND HAS_PERMS_BY_NAME(N''FluxKnowledge'',N''DATABASE'',N''CONNECT'')=1
                                    THEN 1 ELSE 0 END);

            SELECT N''SERVER:''+principal.name
            FROM sys.login_token token
            JOIN sys.server_principals principal ON principal.sid=token.sid
            WHERE principal.type_desc=N''SERVER_ROLE'' AND principal.name<>N''public''
            UNION ALL
            SELECT N''FluxKnowledge:''+principal.name
            FROM [FluxKnowledge].sys.database_role_members membership
            JOIN [FluxKnowledge].sys.database_principals principal
              ON principal.principal_id=membership.role_principal_id
            JOIN [FluxKnowledge].sys.database_principals member
              ON member.principal_id=membership.member_principal_id
            WHERE member.sid=SUSER_SID()
            ORDER BY 1;

            SELECT N''SERVER:''+permission.permission_name
            FROM sys.server_permissions permission
            WHERE permission.grantee_principal_id=SUSER_ID() AND permission.state IN (N''G'',N''W'')
              AND (permission.permission_name LIKE N''ALTER %'' OR permission.permission_name LIKE N''CREATE %'' OR
                   permission.permission_name IN (N''CONTROL SERVER'',N''IMPERSONATE ANY LOGIN'',N''TAKE OWNERSHIP''))
            UNION ALL
            SELECT N''FluxKnowledge:''+permission.permission_name
            FROM [FluxKnowledge].sys.database_permissions permission
            JOIN [FluxKnowledge].sys.database_principals principal
              ON principal.principal_id=permission.grantee_principal_id
            WHERE principal.sid=SUSER_SID() AND permission.state IN (N''G'',N''W'')
            ORDER BY 1;

            CREATE TABLE #Authority (
                SubjectPrincipal nvarchar(256) NOT NULL,
                ScopeName nvarchar(128) NOT NULL,
                SourcePrincipal nvarchar(256) NOT NULL,
                SourcePrincipalType nvarchar(128) NOT NULL,
                AuthorityKind nvarchar(32) NOT NULL,
                Authority nvarchar(512) NOT NULL);

            INSERT #Authority
            SELECT N''IIS AppPool\FluxKnowledge'',N''SERVER'',principal.name,principal.type_desc,N''ROLE'',principal.name
            FROM sys.login_token token
            JOIN sys.server_principals principal ON principal.sid=token.sid
            WHERE principal.type_desc=N''SERVER_ROLE'' AND principal.name<>N''public'';

            INSERT #Authority
            SELECT N''IIS AppPool\FluxKnowledge'',N''SERVER'',principal.name,principal.type_desc,
                   CASE WHEN permission.permission_name LIKE N''ALTER %'' OR permission.permission_name LIKE N''CREATE %'' OR
                                  permission.permission_name IN (N''CONTROL SERVER'',N''IMPERSONATE ANY LOGIN'',N''TAKE OWNERSHIP'')
                        THEN N''DDL'' ELSE N''PERMISSION'' END,
                   permission.class_desc+N'':''+CONVERT(nvarchar(12),permission.major_id)+N'':''+
                       permission.permission_name+N'':''+permission.state_desc
            FROM sys.server_permissions permission
            JOIN sys.server_principals principal ON principal.principal_id=permission.grantee_principal_id
            JOIN sys.login_token token ON token.sid=principal.sid
            WHERE principal.principal_id<>SUSER_ID() AND permission.state IN (N''G'',N''W'') AND
                  (principal.name<>N''public'' OR permission.permission_name LIKE N''ALTER %'' OR
                   permission.permission_name LIKE N''CREATE %'' OR
                   permission.permission_name IN (N''CONTROL SERVER'',N''IMPERSONATE ANY LOGIN'',N''TAKE OWNERSHIP''));

            USE [master];
            INSERT #Authority
            SELECT N''IIS AppPool\FluxKnowledge'',N''master'',principal.name,principal.type_desc,N''ROLE'',principal.name
            FROM sys.user_token token
            JOIN sys.database_principals principal ON principal.sid=token.sid
            WHERE principal.type_desc=N''DATABASE_ROLE'' AND principal.name<>N''public'';

            INSERT #Authority
            SELECT N''IIS AppPool\FluxKnowledge'',N''master'',principal.name,principal.type_desc,
                   CASE WHEN permission.permission_name LIKE N''ALTER %'' OR permission.permission_name LIKE N''CREATE %'' OR
                                  permission.permission_name IN (N''CONTROL'',N''IMPERSONATE'',N''TAKE OWNERSHIP'')
                        THEN N''DDL'' ELSE N''PERMISSION'' END,
                   permission.class_desc+N'':''+CONVERT(nvarchar(12),permission.major_id)+N'':''+
                       CONVERT(nvarchar(12),permission.minor_id)+N'':''+permission.permission_name+N'':''+permission.state_desc
            FROM sys.database_permissions permission
            JOIN sys.database_principals principal ON principal.principal_id=permission.grantee_principal_id
            JOIN sys.user_token token ON token.sid=principal.sid
            WHERE permission.state IN (N''G'',N''W'') AND
                  (principal.principal_id=DATABASE_PRINCIPAL_ID() OR
                   principal.name<>N''public'' OR permission.permission_name LIKE N''ALTER %'' OR
                    permission.permission_name LIKE N''CREATE %'' OR
                    permission.permission_name IN (N''CONTROL'',N''IMPERSONATE'',N''TAKE OWNERSHIP'') OR
                    principal.name=N''public'' AND permission.permission_name=N''EXECUTE'' AND
                       permission.class_desc IN (N''DATABASE'',N''SCHEMA''));

            USE [FluxKnowledge];
            INSERT #Authority
            SELECT N''IIS AppPool\FluxKnowledge'',N''FluxKnowledge'',principal.name,principal.type_desc,N''ROLE'',principal.name
            FROM sys.user_token token
            JOIN sys.database_principals principal ON principal.sid=token.sid
            WHERE principal.type_desc=N''DATABASE_ROLE'' AND principal.name<>N''public'';

            INSERT #Authority
            SELECT N''IIS AppPool\FluxKnowledge'',N''FluxKnowledge'',principal.name,principal.type_desc,
                   CASE WHEN permission.permission_name LIKE N''ALTER %'' OR permission.permission_name LIKE N''CREATE %'' OR
                                  permission.permission_name IN (N''CONTROL'',N''IMPERSONATE'',N''TAKE OWNERSHIP'')
                        THEN N''DDL'' ELSE N''PERMISSION'' END,
                   permission.class_desc+N'':''+CONVERT(nvarchar(12),permission.major_id)+N'':''+
                       CONVERT(nvarchar(12),permission.minor_id)+N'':''+permission.permission_name+N'':''+permission.state_desc
            FROM sys.database_permissions permission
            JOIN sys.database_principals principal ON principal.principal_id=permission.grantee_principal_id
            JOIN sys.user_token token ON token.sid=principal.sid
            WHERE principal.principal_id<>DATABASE_PRINCIPAL_ID() AND permission.state IN (N''G'',N''W'') AND
                  (principal.name<>N''public'' OR permission.permission_name LIKE N''ALTER %'' OR
                   permission.permission_name LIKE N''CREATE %'' OR
                   permission.permission_name IN (N''CONTROL'',N''IMPERSONATE'',N''TAKE OWNERSHIP'') OR
                   principal.name=N''public'' AND permission.permission_name=N''EXECUTE'' AND
                       permission.class_desc IN (N''DATABASE'',N''SCHEMA''));

            SELECT SubjectPrincipal,ScopeName,SourcePrincipal,SourcePrincipalType,AuthorityKind,Authority
            FROM #Authority ORDER BY ScopeName,SourcePrincipal,AuthorityKind,Authority;
            REVERT;
        END TRY
        BEGIN CATCH
            IF ORIGINAL_LOGIN()<>SUSER_SNAME() REVERT;
            THROW;
        END CATCH;');
    END TRY
    BEGIN CATCH
        EXEC(N'REVOKE EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveObserveAppPool FROM '+
             QUOTENAME(@BootstrapLogin)+N';');
        THROW;
    END CATCH;

    EXEC(N'REVOKE EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveObserveAppPool FROM '+
         QUOTENAME(@BootstrapLogin)+N';');
END;
GO
-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveObserveAppPool

IF EXISTS (
    SELECT 1
    FROM sys.database_permissions procedure_permission
    JOIN sys.procedures procedure_object ON procedure_object.object_id=procedure_permission.major_id
    JOIN sys.schemas procedure_schema ON procedure_schema.schema_id=procedure_object.schema_id
    WHERE procedure_permission.class_desc=N'OBJECT_OR_COLUMN' AND
          procedure_schema.name=N'dbo' AND procedure_object.name IN (
              N'FluxKnowledgeNativeGoLiveCreate',
              N'FluxKnowledgeNativeGoLiveDrop',
              N'FluxKnowledgeNativeGoLiveManageAppPool',
              N'FluxKnowledgeNativeGoLiveObserveAppPool'))
    THROW 51000, 'native-go-live-bootstrap-procedure-permission-exists', 1;
GO

DECLARE @Certificate sysname = N'FluxKnowledgeNativeGoLiveCertificate';
DECLARE @Procedure sysname;
DECLARE procedures CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES
        (N'FluxKnowledgeNativeGoLiveCreate'),
        (N'FluxKnowledgeNativeGoLiveDrop'),
        (N'FluxKnowledgeNativeGoLiveManageAppPool'),
        (N'FluxKnowledgeNativeGoLiveObserveAppPool')) value(name);
OPEN procedures;
FETCH NEXT FROM procedures INTO @Procedure;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC(N'ADD SIGNATURE TO OBJECT::dbo.'+QUOTENAME(@Procedure)+N' BY CERTIFICATE '+QUOTENAME(@Certificate)+N';');
    FETCH NEXT FROM procedures INTO @Procedure;
END;
CLOSE procedures;
DEALLOCATE procedures;
GO

DECLARE @BootstrapLogin sysname = N'$(NativeGoLiveBootstrapLogin)';
IF @BootstrapLogin=N'__SUPPLY_AT_EXECUTION__' OR SUSER_ID(@BootstrapLogin) IS NULL
    THROW 51000, 'native-go-live-bootstrap-login-missing', 1;
EXEC(N'GRANT CONNECT SQL TO '+QUOTENAME(@BootstrapLogin)+N';');
IF DATABASE_PRINCIPAL_ID(@BootstrapLogin) IS NULL
    EXEC(N'CREATE USER '+QUOTENAME(@BootstrapLogin)+N' FOR LOGIN '+QUOTENAME(@BootstrapLogin)+N';');
EXEC(N'GRANT CONNECT TO '+QUOTENAME(@BootstrapLogin)+N';');
EXEC(N'GRANT EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveCreate TO '+QUOTENAME(@BootstrapLogin)+N';');
EXEC(N'GRANT EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveDrop TO '+QUOTENAME(@BootstrapLogin)+N';');
EXEC(N'GRANT EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveManageAppPool TO '+QUOTENAME(@BootstrapLogin)+N';');
EXEC(N'GRANT EXECUTE ON OBJECT::dbo.FluxKnowledgeNativeGoLiveObserveAppPool TO '+QUOTENAME(@BootstrapLogin)+N';');
GO
