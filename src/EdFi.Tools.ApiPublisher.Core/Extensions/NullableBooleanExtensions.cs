// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Extensions;

public static class NullableBooleanExtensions
{
    public static bool IsTrue(this bool? value)
    {
        return (value == true);
    }

    public static bool IsNotTrue(this bool? value)
    {
        return value != true;
    }

    public static bool IsFalse(this bool? value)
    {
        return (value == false);
    }

    public static bool IsNotFalse(this bool? value)
    {
        return value != false;
    }
}
