-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

CREATE OR ALTER PROCEDURE dbo.SetConfigurationValue 
	@configurationKey nvarchar(450), 
	@plaintext nvarchar(max),
	@encrypt bit = 0
	WITH EXECUTE AS 'EdFiEncryption'
AS
BEGIN
	SET NOCOUNT ON;

	BEGIN TRANSACTION

	DELETE FROM dbo.ConfigurationValue
	WHERE ConfigurationKey = @configurationKey;

	IF @encrypt = 0
		INSERT INTO dbo.ConfigurationValue(ConfigurationKey, ConfigurationValue)
		VALUES (@configurationKey, @plaintext)
	ELSE
		BEGIN
			-- Open the symmetric key with which to encrypt the data.
			OPEN SYMMETRIC KEY EdFiApiPublisherConfigKey
			DECRYPTION BY CERTIFICATE EdFiApiPublisherConfigCert;

			DECLARE @encrypted varbinary(max);

			-- Encrypt the value
			SET @encrypted = EncryptByKey(Key_GUID('EdFiApiPublisherConfigKey'), 
				@plaintext, 1, HashBytes('SHA1', CONVERT(varbinary, @configurationKey)));

			INSERT INTO dbo.ConfigurationValue(ConfigurationKey, ConfigurationValueEncrypted)
			VALUES (@configurationKey, @encrypted)

			CLOSE SYMMETRIC KEY EdFiApiPublisherConfigKey
		END 

	COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE dbo.GetConfigurationValues
	@configurationKeyPrefix nvarchar(450) = null
	WITH EXECUTE AS 'EdFiEncryption'
AS
BEGIN
	SET NOCOUNT ON;

	-- Open the symmetric key with which to encrypt the data.
	OPEN SYMMETRIC KEY EdFiApiPublisherConfigKey
	DECRYPTION BY CERTIFICATE EdFiApiPublisherConfigCert;

	SELECT c.ConfigurationKey, 
		COALESCE(c.ConfigurationValue, 
			Convert(nvarchar, DecryptByKey(c.ConfigurationValueEncrypted, 1, HashBytes('SHA1', CONVERT(varbinary, c.ConfigurationKey)))))
			AS ConfigurationValue
	FROM dbo.ConfigurationValue c
	WHERE ConfigurationKey LIKE COALESCE(@configurationKeyPrefix, '') + '%'

	CLOSE SYMMETRIC KEY EdFiApiPublisherConfigKey
END
GO
