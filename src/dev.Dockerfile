# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.


# tag sdk:10.0.11-alpine3.24
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1 AS build
WORKDIR /source

COPY ./.editorconfig .editorconfig
COPY ./EdFi.Tools.ApiPublisher.Cli/ EdFi.Tools.ApiPublisher.Cli/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.Aws/ EdFi.Tools.ApiPublisher.ConfigurationStore.Aws/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext/ EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql/ EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer/ EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer/
COPY ./EdFi.Tools.ApiPublisher.Connections.Api/ EdFi.Tools.ApiPublisher.Connections.Api/
COPY ./EdFi.Tools.ApiPublisher.Connections.Sqlite/ EdFi.Tools.ApiPublisher.Connections.Sqlite/
COPY ./EdFi.Tools.ApiPublisher.Core/ EdFi.Tools.ApiPublisher.Core/

RUN dotnet restore EdFi.Tools.ApiPublisher.Cli/EdFi.Tools.ApiPublisher.Cli.csproj

COPY ./EdFi.Tools.ApiPublisher.Cli/ EdFi.Tools.ApiPublisher.Cli/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.Aws/ EdFi.Tools.ApiPublisher.ConfigurationStore.Aws/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext/ EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql/ EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql/
COPY ./EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer/ EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer/
COPY ./EdFi.Tools.ApiPublisher.Connections.Api/ EdFi.Tools.ApiPublisher.Connections.Api/
COPY ./EdFi.Tools.ApiPublisher.Connections.Sqlite/ EdFi.Tools.ApiPublisher.Connections.Sqlite/
COPY ./EdFi.Tools.ApiPublisher.Core/ EdFi.Tools.ApiPublisher.Core/


WORKDIR /source/EdFi.Tools.ApiPublisher.Cli
RUN dotnet build -c Release
FROM build AS publish
RUN dotnet publish -c Release -o /app/EdFi.Tools.ApiPiblisher.Cli --no-build --nologo


# Tag aspnet:10.0.11-alpine3.24
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8
LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

# Alpine image does not contain Globalization Cultures library so we need to install ICU library to get fopr LINQ expression to work
# Disable the globaliztion invariant mode (set in base image)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_ENVIRONMENT Development

WORKDIR /app
COPY --from=publish /app/EdFi.Tools.ApiPiblisher.Cli/ .
COPY ./Docker/apiPublisherSettings.template.json /app/apiPublisherSettings.template.json
COPY ./Docker/configurationStoreSettings.template.json /app/configurationStoreSettings.template.json
COPY ./Docker/logging.template.json /app/logging.template.json
COPY ./Docker/plainTextNamedConnections.template.json /app/plainTextNamedConnections.template.json
COPY ./Docker/run.sh /app/run.sh

RUN apk --no-cache add --upgrade unzip=~6 dos2unix=~7 bash=~5 openssl=3.5.7-r0 gettext=~1 icu=78.1-r0 curl=~8 && \
    dos2unix /app/*.json && \
    dos2unix /app/*.sh && \
    chmod 700 /app/*.sh -- ** && \
    rm -f /app/*.pdb && \
    rm -f /app/*.exe

ENTRYPOINT [ "/app/run.sh" ]
