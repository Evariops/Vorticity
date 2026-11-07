--------------------------- MODULE DatasetCommitRuns ---------------------------
(***************************************************************************)
(* Runs of the real code that DatasetScheduleTests recorded, each the      *)
(* versions one order of a scenario created, in the model's terms: per    *)
(* level, each object named after its writer, the rows it holds, the rows  *)
(* its deletion vector marks, and the keys it spans. The first version is *)
(* the seeded one every run starts from. ResurrectedRow is made by hand.  *)
(***************************************************************************)
EXTENDS Sequences, TLC

\* Scenario "appends", picks 0,0,1,1,0,0,0,0: a1 appends key 1, the job merges it alone into level 1,
\* a2 appends key 2, and a second job merges it with the seed.
RunAppends ==
  <<(0 :> {} @@ 1 :> {[uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {[uid |-> <<"append", "a1">>, rows |-> {"a1"}, marked |-> {}, lo |-> 1, hi |-> 1]} @@ 1 :> {[uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {} @@ 1 :> {[uid |-> <<"job", "c1", 1>>, rows |-> {"a1"}, marked |-> {}, lo |-> 1, hi |-> 1], [uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {[uid |-> <<"append", "a2">>, rows |-> {"a2"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 1 :> {[uid |-> <<"job", "c1", 1>>, rows |-> {"a1"}, marked |-> {}, lo |-> 1, hi |-> 1], [uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {} @@ 1 :> {[uid |-> <<"job", "c1", 1>>, rows |-> {"a1"}, marked |-> {}, lo |-> 1, hi |-> 1], [uid |-> <<"job", "c1", 2>>, rows |-> {"a2", "seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {})>>

\* Scenario "delete", picks 2,2,0,0,0,0: d1 deletes key 2 and takes the seed out whole, a1 appends key 2,
\* and the job merges it into level 1.
RunDelete ==
  <<(0 :> {} @@ 1 :> {[uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {} @@ 1 :> {} @@ 2 :> {}),
  (0 :> {[uid |-> <<"append", "a1">>, rows |-> {"a1"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 1 :> {} @@ 2 :> {}),
  (0 :> {} @@ 1 :> {[uid |-> <<"job", "c1", 1>>, rows |-> {"a1"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {})>>

\* Scenario "marks", picks 0,0,0,0,0,0,0: a1 and a2 append keys 1 and 2, the job merges both with the seed,
\* and d1, deleting key 2, marks two of the merged object's three rows.
RunMarks ==
  <<(0 :> {} @@ 1 :> {[uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {[uid |-> <<"append", "a1">>, rows |-> {"a1"}, marked |-> {}, lo |-> 1, hi |-> 1]} @@ 1 :> {[uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {[uid |-> <<"append", "a1">>, rows |-> {"a1"}, marked |-> {}, lo |-> 1, hi |-> 1], [uid |-> <<"append", "a2">>, rows |-> {"a2"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 1 :> {[uid |-> <<"seed">>, rows |-> {"seed"}, marked |-> {}, lo |-> 2, hi |-> 2]} @@ 2 :> {}),
  (0 :> {} @@ 1 :> {[uid |-> <<"job", "c1", 1>>, rows |-> {"a1", "a2", "seed"}, marked |-> {}, lo |-> 1, hi |-> 2]} @@ 2 :> {}),
  (0 :> {} @@ 1 :> {[uid |-> <<"job", "c1", 1>>, rows |-> {"a1", "a2", "seed"}, marked |-> {"a2", "seed"}, lo |-> 1, hi |-> 2]} @@ 2 :> {})>>

\* By hand: RunMarks, then a second job that writes the merged object into level 2 with the rows the delete
\* marked, as a job that read it before the delete and did not check its entry would.
ResurrectedRow ==
  RunMarks \o
  <<(0 :> {} @@ 1 :> {} @@ 2 :> {[uid |-> <<"job", "c1", 2>>, rows |-> {"a1", "a2", "seed"}, marked |-> {}, lo |-> 1, hi |-> 2]})>>
================================================================================
