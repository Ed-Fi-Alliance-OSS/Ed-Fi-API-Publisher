#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

set -e
set +x

# Default to automatic processing block bounded capacity when not provided (see APIPUB-112)
export PROCESSING_BLOCK_BOUNDED_CAPACITY="${PROCESSING_BLOCK_BOUNDED_CAPACITY:-0}"

# Default to cursor paging enabled when not provided (see APIPUB-139)
export DISABLE_CURSOR_PAGING="${DISABLE_CURSOR_PAGING:-false}"

# Default to the automatic partition count (JSON null) when not provided; the template emits the value as a bare
# JSON token, so an empty substitution would produce an invalid settings file (see APIPUB-139)
export CURSOR_PAGING_PARTITION_COUNT="${CURSOR_PAGING_PARTITION_COUNT:-null}"

envsubst < /app/apiPublisherSettings.template.json > /app/apiPublisherSettings.json
envsubst < /app/logging.template.json > /app/logging.json
envsubst < /app/configurationStoreSettings.template.json > /app/configurationStoreSettings.json
envsubst < /app/plainTextNamedConnections.template.json > /app/plainTextNamedConnections.json

tail -f /dev/null