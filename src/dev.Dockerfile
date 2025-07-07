# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.


# tag sdk:8.0 alpine
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine3.22@sha256:071ec6075f01f91ceaef8f1eaed5d43873635d46441f99221473c456f37f8c20 AS build
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


# Tag aspnet:8.0 alpine
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine3.22@sha256:46be98e65c56f07528fd70dcd0e684b9fe762f4c7256402fcabb8daf45076ddf
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

RUN apk --no-cache add --upgrade unzip=~6 dos2unix=~7 bash=~5 openssl=3.5.1-r0 gettext=~0 icu=76.1-r0 curl=~8 && \
    dos2unix /app/*.json && \
    dos2unix /app/*.sh && \
    chmod 700 /app/*.sh -- ** && \
    rm -f /app/*.pdb && \
    rm -f /app/*.exe

ENTRYPOINT [ "/app/run.sh" ]
