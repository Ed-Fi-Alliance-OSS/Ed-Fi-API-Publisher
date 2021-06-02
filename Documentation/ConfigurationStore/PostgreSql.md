# PostgreSQL Configuration Store

Enables management of individual connection settings with encryption for securely storing keys and secrets using a supplied password for symmetric key encryption using the `pgcrypto` extension for PostgreSQL.

## Create the Ed-Fi API Publisher Configuration Database and Table

```sql
create database edfi_api_publisher_configuration;
create schema dbo;

create table dbo.configuration_value
(
    configuration_key varchar(450) not null
        constraint configuration_value_pk
            primary key,
    configuration_value text,
    configuration_value_encrypted bytea
);

create extension if not exists pgcrypto;
```

## Configure API Connections

Create new API connections by executing SQL statements similar to what is shown below. The `pgcrypto` functions used below employ symmetric key encryption based on the supplied password.

For the API Publisher, the password can be supplied via the command-line using `--postgreSqlEncryptionPassword`, accessed through an environment variable named `EdFi:ApiPublisher:ConfigurationStore:PostgreSql:EncryptionPassword` (or `EdFi__ApiPublisher__ConfigurationStore__PostgreSql__EncryptionPassword`), or through the _configurationStoreSettings.json_ file.

```sql
-- Insert plain text values into the 'configuration_value' column
insert into dbo.configuration_value(configuration_key, configuration_value)
values ('/ed-fi/publisher/connections/EdFi_Hosted_Sample_v5.2/url', 'https://api.ed-fi.org/v5.2/api/');

-- Insert encrypted values into 'configuration_value_encrypted' column
insert into dbo.configuration_value(configuration_key, configuration_value_encrypted)
values ('/ed-fi/publisher/connections/EdFi_Hosted_Sample_v5.2/key', pgp_sym_encrypt('RvcohKz9zHI4', 'my-secure-password'));

insert into dbo.configuration_value(configuration_key, configuration_value_encrypted)
values ('/ed-fi/publisher/connections/EdFi_Hosted_Sample_v5.2/secret', pgp_sym_encrypt('E1iEFusaNf81xzCxwHfbolkC', 'my-secure-password'));
```

## Configure API Publisher

To use the PostgreSQL Configuration Store, change the `provider` setting in the _configurationStoreSettings.json_ file to `postgreSql`:

```json
{
  "configurationStore": {
    "provider": "postgreSql",
```
