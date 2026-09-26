-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Admin bootstrap for one ODS/API regression arm (ods-arm.yml): two ODS instances on the db-ods host
-- (populated = publisher source, minimal = publisher target), one vendor, one application per role and
-- one API client per application. Idempotent: safe to re-run. Applies to ODS/API 7.1 through 7.3.
--
-- psql variables (set by Start-RegressionArm from the arm's .env file):
--   pw, source_db, target_db, source_key, source_secret, source_claimset, target_key, target_secret, target_claimset,
--   edorgs (comma-separated EducationOrganizationIds to associate with both applications)

insert into dbo.OdsInstances (Name, InstanceType, ConnectionString)
select 'regression-source', 'ODS', 'host=db-ods;port=5432;username=postgres;password=' || :'pw' || ';database=' || :'source_db' || ';application name=EdFi.Ods.WebApi'
where not exists (select 1 from dbo.OdsInstances where Name = 'regression-source');

insert into dbo.OdsInstances (Name, InstanceType, ConnectionString)
select 'regression-target', 'ODS', 'host=db-ods;port=5432;username=postgres;password=' || :'pw' || ';database=' || :'target_db' || ';application name=EdFi.Ods.WebApi'
where not exists (select 1 from dbo.OdsInstances where Name = 'regression-target');

insert into dbo.Vendors (VendorName)
select 'Regression Vendor'
where not exists (select 1 from dbo.Vendors where VendorName = 'Regression Vendor');

insert into dbo.VendorNamespacePrefixes (NamespacePrefix, Vendor_VendorId)
select p.prefix, v.VendorId
from dbo.Vendors v
cross join (values ('uri://ed-fi.org'), ('uri://gbisd.edu'), ('uri://gbisd.org'), ('uri://tpdm.ed-fi.org')) as p(prefix)
where v.VendorName = 'Regression Vendor'
  and not exists (select 1 from dbo.VendorNamespacePrefixes x where x.NamespacePrefix = p.prefix and x.Vendor_VendorId = v.VendorId);

insert into dbo.Applications (ApplicationName, OperationalContextUri, Vendor_VendorId, ClaimSetName)
select a.name, 'uri://ed-fi.org', v.VendorId, a.claimset
from dbo.Vendors v
cross join (values ('Regression Source', :'source_claimset'), ('Regression Target', :'target_claimset')) as a(name, claimset)
where v.VendorName = 'Regression Vendor'
  and not exists (select 1 from dbo.Applications x where x.ApplicationName = a.name);

-- Every top-level education organization of the source template (Start-RegressionArm reads them from the ODS:
-- the LEA and ESC of Grand Bend plus the standalone schools, community and post-secondary organizations the
-- TPDM sample data hangs off), so relationship-based authorization lets the clients read and write all of it.
insert into dbo.ApplicationEducationOrganizations (EducationOrganizationId, Application_ApplicationId)
select e.edorg, a.ApplicationId
from dbo.Applications a
cross join (select unnest(string_to_array(:'edorgs', ','))::bigint as edorg) as e
where a.ApplicationName in ('Regression Source', 'Regression Target')
  and not exists (select 1 from dbo.ApplicationEducationOrganizations x where x.EducationOrganizationId = e.edorg and x.Application_ApplicationId = a.ApplicationId);

insert into dbo.ApiClients (Key, Secret, Name, IsApproved, UseSandbox, SandboxType, SecretIsHashed, Application_ApplicationId)
select c.key, c.secret, c.name, true, false, 0, false, a.ApplicationId
from (values (:'source_key', :'source_secret', 'Regression Source Client', 'Regression Source'),
             (:'target_key', :'target_secret', 'Regression Target Client', 'Regression Target')) as c(key, secret, name, app)
inner join dbo.Applications a on a.ApplicationName = c.app
where not exists (select 1 from dbo.ApiClients x where x.Name = c.name);

insert into dbo.ApiClientApplicationEducationOrganizations (ApiClient_ApiClientId, ApplicationEdOrg_ApplicationEdOrgId)
select c.ApiClientId, aeo.ApplicationEducationOrganizationId
from dbo.ApiClients c
inner join dbo.Applications a on a.ApplicationId = c.Application_ApplicationId
inner join dbo.ApplicationEducationOrganizations aeo on aeo.Application_ApplicationId = a.ApplicationId
where c.Name in ('Regression Source Client', 'Regression Target Client')
  and not exists (select 1 from dbo.ApiClientApplicationEducationOrganizations x
                  where x.ApiClient_ApiClientId = c.ApiClientId and x.ApplicationEdOrg_ApplicationEdOrgId = aeo.ApplicationEducationOrganizationId);

insert into dbo.ApiClientOdsInstances (ApiClient_ApiClientId, OdsInstance_OdsInstanceId)
select c.ApiClientId, o.OdsInstanceId
from dbo.ApiClients c
inner join dbo.OdsInstances o on o.Name = case c.Name when 'Regression Source Client' then 'regression-source' else 'regression-target' end
where c.Name in ('Regression Source Client', 'Regression Target Client')
  and not exists (select 1 from dbo.ApiClientOdsInstances x
                  where x.ApiClient_ApiClientId = c.ApiClientId and x.OdsInstance_OdsInstanceId = o.OdsInstanceId);

select c.Name as api_client, c.Key, a.ClaimSetName, o.Name as ods_instance
from dbo.ApiClients c
inner join dbo.Applications a on a.ApplicationId = c.Application_ApplicationId
inner join dbo.ApiClientOdsInstances co on co.ApiClient_ApiClientId = c.ApiClientId
inner join dbo.OdsInstances o on o.OdsInstanceId = co.OdsInstance_OdsInstanceId
where c.Name in ('Regression Source Client', 'Regression Target Client')
order by c.Name;
