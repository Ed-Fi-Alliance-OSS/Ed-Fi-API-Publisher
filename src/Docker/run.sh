#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

envsubst < /app/apiPublisherSettings.template.json > /app/apiPublisherSettings.json
envsubst < /app/logging.template.json > /app/logging.json
envsubst < /app/configurationStoreSettings.template.json > /app/configurationStoreSettings.json
envsubst < /app/plainTextNamedConnections.template.json > /app/plainTextNamedConnections.json

# dotnet EdFiApiPublisher.dll --sourceUrl=https://api.ed-fi.org/v5.2/api/ --sourceKey=RvcohKz9zHI4 --sourceSecret=E1iEFusaNf81xzCxwHfbolkC --targetUrl=http://localhost:54746/ --targetKey=rfiT5McXOVR9hFvtbEKx3 --targetSecret=1yIRA0kvT6ENxtB9OGbcf --ignoreIsolation=true --maxDegreeOfParallelismForPostResourceItem=5 --maxDegreeOfParallelismForStreamResourcePages=3 --includeDescriptors=true --exclude=surveys

tail -f /dev/null