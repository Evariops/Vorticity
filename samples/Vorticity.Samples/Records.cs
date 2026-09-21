using System;

namespace Vorticity.Samples;

/// <summary>A day, a temperature that may be missing, and a city: the record most pages read.</summary>
[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);

/// <summary>A visit to a site: a uuid, a timestamp, a nullable text, a list and a nested record.</summary>
[VortexRecord]
public partial record struct Visit(Guid Id, DateTime StartedAt, int DurationMs, string? Referrer, ReadOnlyMemory<int> Pages, Address Origin);

/// <summary>Where a visit came from.</summary>
[VortexRecord]
public partial record struct Address(string Country, string? City);
