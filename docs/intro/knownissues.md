## Known Limitations for Ed-Fi ODS / API 5.1 through 5.3

Currently, Ed-Fi ODS / API 5.1 through 5.3 has the following known issues related to Change Queries and the Ed-Fi API Publisher.  These have been resolved in [Ed-Fi ODS / API 5.3-cqe patch](https://techdocs.ed-fi.org/display/EFTD/Change+Query+Enhancements) and [Ed-Fi ODS / API 6.1](https://techdocs.ed-fi.org/pages/viewpage.action?pageId=138642238).

* [Change Queries implementation doesn't provide enough information to communicate deletes between ODS databases](https://tracker.ed-fi.org/browse/ODS-3672)
* [Add support to Change Queries for tracking deletes by natural key](https://tracker.ed-fi.org/browse/ODS-4423)
* [Change Queries does not capture deletes on derived resources](https://tracker.ed-fi.org/browse/ODS-4087)
* [Change Queries does not support primary key changes](https://tracker.ed-fi.org/browse/ODS-5005)

(Feedback on the need for resolution to the Ed-Fi ODS API issues listed above should be provided to the Ed-Fi Alliance through [Ed-Fi Support](https://support.ed-fi.org/).)

The Ed-Fi ODS/API only exposes the "Id" of the resources that are deleted, however since the "Id" is not intended to be a global, portable identifier for the resource (Ed-Fi uses domain key values for that identity), and thus the _current implementation_ of the deletes resource is of limited value for API Publishing.

Even with delete support added by exposing the primary key values, tracking and publishing deletions of Descriptors will still not be possible due to internal implementation details within the Ed-Fi ODS API through (at least) v5.3.

Changes to primary keys (on the API resources that support it) in source API will currently result in stale copies of the "old" version of the resources (and all impacted dependencies) remaining in the target API. 

An additional limitation of the Ed-Fi API Publisher is the current lack of support for API Profiles (for defining resource/property level data policies for API clients). Create a support case to request Profiles support if this of interest to you.

