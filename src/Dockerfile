# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Tag aspnet:8.0-alpine3.20
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine3.20@sha256:b5b7dec8006fe016cc864f618cf60eab24fb7d7a28c8ecf4f6b90ceeaa5cf9f2
LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

ARG VERSION="1.2.1"

# Alpine image does not contain Globalization Cultures library so we need to install ICU library to get fopr LINQ expression to work
# Disable the globaliztion invariant mode (set in base image)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app
COPY ./Docker/apiPublisherSettings.template.json /app/apiPublisherSettings.template.json
COPY ./Docker/configurationStoreSettings.template.json /app/configurationStoreSettings.template.json
COPY ./Docker/logging.template.json /app/logging.template.json
COPY ./Docker/plainTextNamedConnections.template.json /app/plainTextNamedConnections.template.json

COPY ./Docker/run.sh /app/run.sh

# Security packages are pinned to exact versions to prevent automatic minor upgrades
# that could introduce breaking changes. Update manually when security patches are available:
# - openssl: Currently pinned to 3.3.3-r0 (latest as of Alpine 3.20)
# - musl: Pinned to 1.2.5-r1 to address CVE-2025-26519
RUN apk update && \
    apk --no-cache add --upgrade unzip=~6 dos2unix=~7 bash=~5 gettext=~0 openssl=3.3.3-r0 postgresql16-client=~16 icu=~74 curl=~8 musl=1.2.5-r1 && \
    wget -nv -O /app/ApiPublisher.zip https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.ApiPublisher/versions/${VERSION}/content && \
    unzip /app/ApiPublisher.zip 'EdFi.ApiPublisher/**' -d /app/ && \
    mv /app/EdFi.ApiPublisher/* /app/ && \
    rmdir /app/EdFi.ApiPublisher && \
    rm -f /app/ApiPublisher.zip && \
    dos2unix /app/*.json && \
    dos2unix /app/*.sh && \
    chmod 700 /app/*.sh -- ** && \
    rm -f /app/*.exe

ENTRYPOINT [ "/app/run.sh" ]

