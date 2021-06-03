DECLARE 
    @applicationId INT, 
    @applicationEducationOrganizationId INT,
    @apiClientId INT

BEGIN TRANSACTION

-- Get the ApplicationId for the minimal sandbox
SELECT  @applicationId = ApplicationId
FROM    EdFi_Admin.dbo.Applications
WHERE   ApplicationName = 'Default Sandbox Application Minimal'

IF (@applicationId IS NULL)
    THROW 50000, 'Unable to find the minimal sandbox application in the EdFi_Admin database.', 1

-- Set the claim set associated with the minimal sandbox to the API Publisher writer
UPDATE  EdFi_Admin.dbo.Applications
SET     ClaimSetName = 'Ed-Fi API Publisher - Writer'
WHERE   ApplicationId = @applicationId

-- Add association of Application to Grand Bend ISD
INSERT INTO EdFi_Admin.dbo.ApplicationEducationOrganizations(Application_ApplicationId, EducationOrganizationId)
VALUES (@applicationId, 255901)

SELECT @applicationEducationOrganizationId = SCOPE_IDENTITY()

-- Get the API client for application's minimal sandbox
SELECT  @apiClientId = ApiClientId
FROM    EdFi_Admin.dbo.ApiClients
WHERE   Application_ApplicationId = @applicationId
        AND Name = 'Minimal Demonstration Sandbox'

IF @apiClientId IS NULL
    THROW 50000, 'Unable to find the API client for the minimal sandbox application.', 1

-- Associate the API Client with Grand Bend ISD
INSERT INTO EdFi_Admin.dbo.ApiClientApplicationEducationOrganizations(ApiClient_ApiClientId, ApplicationEducationOrganization_ApplicationEducationOrganizationId)
VALUES (@apiClientId, @applicationEducationOrganizationId)

COMMIT TRANSACTION
