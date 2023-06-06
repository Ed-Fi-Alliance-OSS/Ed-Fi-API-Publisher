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

tail -f /dev/null