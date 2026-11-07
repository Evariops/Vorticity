-------------------------- MODULE DatasetCommitTrace --------------------------
(***************************************************************************)
(* A run of the real code, judged against the model of the commit, as      *)
(* Cirstea, Kuppe, Merz and Loillier validate the traces of a program      *)
(* against its TLA+ specification: what the run left in the store, the     *)
(* versions it created in the model's terms, constrains the model's        *)
(* history, and TLC searches the rest of the model's state, the writers'   *)
(* plans, reads, attempts and the keys of their rows, for a behaviour that *)
(* creates those versions in that order.                                    *)
(*                                                                          *)
(* DatasetScheduleTests writes the runs, one definition each, in a module  *)
(* this one extends. NotFollowed is violated, TLC exiting with 12, when    *)
(* the model follows a run to its last version: the run is a behaviour of  *)
(* the model. It holds, TLC exiting with 0, when no behaviour of the model *)
(* creates those versions: the code did what the model does not allow.    *)
(***************************************************************************)
EXTENDS DatasetCommit, TLC, DatasetCommitRuns

CONSTANT TraceHistory   \* the versions a run created, the first one the version it started from

\* The model's history is a prefix of the run's.
Follows(h) == Len(h) <= Len(TraceHistory) /\ h = SubSeq(TraceHistory, 1, Len(h))

TraceInit == Init /\ Follows(history)
TraceNext == Next /\ Follows(history')
TraceSpec == TraceInit /\ [][TraceNext]_vars

\* Violated once the model has created every version of the run.
NotFollowed == Len(history) < Len(TraceHistory)
================================================================================
