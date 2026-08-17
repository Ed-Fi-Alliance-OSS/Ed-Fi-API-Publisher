# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Tag aspnet:10.0.11-alpine3.24
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8
LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

ARG VERSION="1.3.0"

# Alpine image does not contain Globalization Cultures library so we need to install ICU library to get fopr LINQ expression to work
# Disable the globaliztion invariant mode (set in base image)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app
COPY ./Docker/apiPublisherSettings.template.json /app/apiPublisherSettings.template.json
COPY ./Docker/configurationStoreSettings.template.json /app/configurationStoreSettings.template.json
COPY ./Docker/logging.template.json /app/logging.template.json
COPY ./Docker/plainTextNamedConnections.template.json /app/plainTextNamedConnections.template.json

COPY ./Docker/run.sh /app/run.sh

RUN apk update && \
    apk --no-cache add --upgrade unzip=~6 dos2unix=~7 bash=~5 gettext=~1 openssl=3.5.7-r0 postgresql16-client=~16 icu=78.1-r0 curl=~8 && \
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

