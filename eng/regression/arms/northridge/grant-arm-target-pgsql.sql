-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Associates the target API client of an ODS arm (arms/bootstrap-pgsql.sql) with the Northridge education
-- organizations, and registers the Northridge namespace prefixes for the arm's vendor, so the arm's target accepts what the publisher reads from the Northridge source. The arm's own
-- bootstrap associates its clients only with the top-level organizations of its own source (Grand Bend), and the
-- target then refuses every Northridge document with 403 "No relationships have been established" (first
-- Northridge run on the AWS host, 2026-10-06). Idempotent; run by Start-NorthridgeSource.ps1 against the arm's
-- EdFi_Admin with psql -v edorgs=<comma-separated ids> -v prefixes=<comma-separated namespace prefixes>.

-- Namespace-based authorization (learning standards, descriptors, assessments) checks the vendor's prefixes, and arm
-- B's bootstrap registers only Grand Bend's (second Northridge run on the AWS host, 2026-10-07: 5,357 learning
-- standards and 72 descriptors refused with 403).
insert into dbo.VendorNamespacePrefixes (NamespacePrefix, Vendor_VendorId)
select p.prefix, v.VendorId
from dbo.Vendors v
cross join (select trim(unnest(string_to_array(:'prefixes', ','))) as prefix) as p
where v.VendorName = 'Regression Vendor'
  and p.prefix <> ''
  and not exists (select 1 from dbo.VendorNamespacePrefixes x where x.NamespacePrefix = p.prefix and x.Vendor_VendorId = v.VendorId);

insert into dbo.ApplicationEducationOrganizations (EducationOrganizationId, Application_ApplicationId)
select e.edorg, a.ApplicationId
from dbo.Applications a
cross join (select unnest(string_to_array(:'edorgs', ','))::bigint as edorg) as e
where a.ApplicationName = 'Regression Target'
  and not exists (select 1 from dbo.ApplicationEducationOrganizations x where x.EducationOrganizationId = e.edorg and x.Application_ApplicationId = a.ApplicationId);

insert into dbo.ApiClientApplicationEducationOrganizations (ApiClient_ApiClientId, ApplicationEdOrg_ApplicationEdOrgId)
select c.ApiClientId, aeo.ApplicationEducationOrganizationId
from dbo.ApiClients c
inner join dbo.ApplicationEducationOrganizations aeo on aeo.Application_ApplicationId = c.Application_ApplicationId
where c.Name = 'Regression Target Client'
  and not exists (select 1 from dbo.ApiClientApplicationEducationOrganizations x
                  where x.ApiClient_ApiClientId = c.ApiClientId and x.ApplicationEdOrg_ApplicationEdOrgId = aeo.ApplicationEducationOrganizationId);

-- The checks the script reads back: the target client's associated organizations, then the vendor's prefixes, each
-- on its own line and comma-separated.
select string_agg(aeo.EducationOrganizationId::text, ',' order by aeo.EducationOrganizationId)
from dbo.ApiClients c
inner join dbo.ApiClientApplicationEducationOrganizations x on x.ApiClient_ApiClientId = c.ApiClientId
inner join dbo.ApplicationEducationOrganizations aeo on aeo.ApplicationEducationOrganizationId = x.ApplicationEdOrg_ApplicationEdOrgId
where c.Name = 'Regression Target Client';

select string_agg(x.NamespacePrefix, ',' order by x.NamespacePrefix)
from dbo.VendorNamespacePrefixes x
inner join dbo.Vendors v on v.VendorId = x.Vendor_VendorId
where v.VendorName = 'Regression Vendor';
