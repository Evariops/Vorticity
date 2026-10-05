using Vorticity;

namespace MustNotCompile;

[VortexRecord]
internal partial record struct Reading(int Day, double? Celsius, string City);

[VortexRecord]
internal partial record struct CityCount(string City, long Count);

[VortexRecord]
internal partial record struct CityDays(string City, int Days);
