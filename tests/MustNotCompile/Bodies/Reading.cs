using Vorticity;

namespace MustNotCompile;

[VortexRecord]
internal partial record struct Reading(int Day, double? Celsius, string City);
