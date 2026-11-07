------------------------------ MODULE DatasetKeys ------------------------------
(***************************************************************************)
(* The data keys of an encrypted dataset across rekeys                      *)
(* (docs/design/18-encryption.md, "Rotation, rekeying and shredding").      *)
(*                                                                          *)
(* A version is sealed under a data key, and keys are numbered in the order *)
(* rekeys draw them. A writer seals its object under the key of the newest *)
(* commit it has read (SealedObjectStore.Adopt), and reads the newest one  *)
(* whenever it refreshes and at every commit, which reads the version it   *)
(* lands on before it puts its own (DatasetCommitter.AttemptAsync). A      *)
(* writer may read, write and commit at any pace: in flight while a rekey  *)
(* lands, or idle since before it. A rekeyer publishes versions sealed    *)
(* under a new key and adding nothing.                                      *)
(*                                                                          *)
(* Refuse is the fix: a commit refuses an object begun under a key older   *)
(* than the one it has just read (RequireCurrentKey).                       *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANTS
    Writers,    \* each writes one object and commits it
    Rekeys,     \* how many rekeys run
    Refuse      \* switch: a commit refuses objects begun under a key older than the one it read

VARIABLES
    versions,   \* each version: the key it is sealed under, and the objects it adds with the key each is sealed under
    known,      \* [Writers -> the key of the newest commit the writer read]
    sealed,     \* [Writers -> the key its object was sealed under, 0 before it writes]
    pc,         \* [Writers -> "write", "commit" or "done"]
    refused,    \* history: the writers whose commit was refused
    rekeyed     \* rekeys done

vars == <<versions, known, sealed, pc, refused, rekeyed>>

Latest == versions[Len(versions)]

Init == /\ versions = <<[key |-> 1, adds |-> {}]>>
        /\ known = [w \in Writers |-> 1]
        /\ sealed = [w \in Writers |-> 0]
        /\ pc = [w \in Writers |-> "write"]
        /\ refused = {}
        /\ rekeyed = 0

\* A refresh: the newest commit read, its key the one the writer seals under from now on.
Refresh(w) ==
    /\ pc[w] # "done"
    /\ known' = [known EXCEPT ![w] = Latest.key]
    /\ UNCHANGED <<versions, sealed, pc, refused, rekeyed>>

\* The writer's object, sealed under the key of the newest commit it read (SealedObjectStore.PutAsync).
Write(w) ==
    /\ pc[w] = "write"
    /\ sealed' = [sealed EXCEPT ![w] = known[w]]
    /\ pc' = [pc EXCEPT ![w] = "commit"]
    /\ UNCHANGED <<versions, known, refused, rekeyed>>

\* The commit: the version it lands on read, its key taken up, then the guard, then the version sealed under it.
Commit(w) ==
    /\ pc[w] = "commit"
    /\ LET k == Latest.key IN
       /\ known' = [known EXCEPT ![w] = k]
       /\ pc' = [pc EXCEPT ![w] = "done"]
       /\ IF Refuse /\ sealed[w] < k
          THEN /\ refused' = refused \cup {w}
               /\ UNCHANGED versions
          ELSE /\ versions' = Append(versions, [key |-> k, adds |-> {<<w, sealed[w]>>}])
               /\ UNCHANGED refused
    /\ UNCHANGED <<sealed, rekeyed>>

\* RekeyAsync: a version that adds nothing, sealed under a key drawn for it.
Rekey ==
    /\ rekeyed < Rekeys
    /\ versions' = Append(versions, [key |-> Latest.key + 1, adds |-> {}])
    /\ rekeyed' = rekeyed + 1
    /\ UNCHANGED <<known, sealed, pc, refused>>

Next == Rekey \/ \E w \in Writers : Refresh(w) \/ Write(w) \/ Commit(w)

Spec == Init /\ [][Next]_vars

\* Every object a version adds is sealed under that version's key: after a rekey, nothing new is
\* sealed under the key it replaced, which a full rekey can then retire.
SealedUnderItsVersion == \A i \in 1..Len(versions) : \A a \in versions[i].adds : a[2] = versions[i].key

\* Witness: a writer's commit is refused.
NoRefusal == refused = {}

\* Witness: an object lands after a rekey, sealed under the new key by a writer that read it.
NoWriteAfterRekey == \A i \in 1..Len(versions) : \A a \in versions[i].adds : a[2] = 1
================================================================================
