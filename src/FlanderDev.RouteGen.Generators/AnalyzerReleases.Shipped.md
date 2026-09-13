## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RG0001 | RouteGen | Error | Duplicate route + verb
RG0002 | RouteGen | Warning | [Body] used with a verb that does not accept a body
RG0003 | RouteGen | Error | Route token has no matching parameter
RG0004 | RouteGen | Error | Parameter does not appear in route template and is not [Query] or [Body]
RG0005 | RouteGen | Error | More than one [Body] parameter
RG0006 | RouteGen | Error | Type is not convertible to/from a URL segment or query string
RG0007 | RouteGen | Error | Ambiguous generated Paths member name
RG0008 | RouteGen | Error | Invalid or unparsable route template
