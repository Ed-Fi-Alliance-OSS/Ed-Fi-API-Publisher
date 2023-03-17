-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.


-- IMPORTANT: Type Ctrl+Shft+M to enter parameters for script.

---------------------------------------------------------
-- Create configuration database
---------------------------------------------------------
CREATE DATABASE [EdFi_API_Publisher_Configuration]
 ON PRIMARY ( NAME = N'EdFi_API_Publisher_Configuration', FILENAME = N'< DataFilePath, nvarchar, C:\MSSQL\MSSQL14.MSSQLSERVER\MSSQL\DATA\EdFi_API_Publisher_Configuration.mdf >' )
 LOG ON ( NAME = N'EdFi_API_Publisher_Configuration_log', FILENAME = N'< LogFilePath, nvarchar, C:\MSSQL\MSSQL14.MSSQLSERVER\MSSQL\DATA\EdFi_API_Publisher_Configuration_log.ldf >')
GO

USE [EdFi_API_Publisher_Configuration]
GO

---------------------------------------------------------
-- Create configuration table
---------------------------------------------------------
CREATE TABLE [dbo].[ConfigurationValue](
	[ConfigurationKey] [nvarchar](450) NOT NULL,
	[ConfigurationValue] [nvarchar](max) NULL,
	[ConfigurationValueEncrypted] [varbinary](max) NULL,
 CONSTRAINT [PK_ConfigurationValue] PRIMARY KEY CLUSTERED ([ConfigurationKey] ASC)
)
GO

---------------------------------------------------------
-- Create encryption certificate and keys
---------------------------------------------------------

-- Create database master key
CREATE MASTER KEY 
ENCRYPTION BY PASSWORD = '< MasterKeyPassword, nvarchar, Hootie#and$the%Blowfish>';

-- Create certificate
CREATE CERTIFICATE EdFiApiPublisherConfigCert
   WITH SUBJECT = 'Ed-Fi API Publisher Configuration Secrets';  

-- Create a symmetric key for encryption
CREATE SYMMETRIC KEY EdFiApiPublisherConfigKey
    WITH ALGORITHM = AES_256  
    ENCRYPTION BY CERTIFICATE EdFiApiPublisherConfigCert;  

-------------------------------------------------------------------
