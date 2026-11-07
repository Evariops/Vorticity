------------------------------- MODULE FilePut -------------------------------
(***************************************************************************)
(* FileObjectStore's put-if-absent of one key, the reads that meet it, and *)
(* the repair of a torn commit (VortexDataset.RemoveTornCommitAsync).      *)
(*                                                                          *)
(* Protocol "claim" is the code at 3da99dca (FileObjectStore.PutAsync):    *)
(* open(O_CREAT|O_EXCL) claims the key, the exclusive flock follows as a   *)
(* second system call (.NET emulates FileShare.None that way on Unix, "not *)
(* atomic with file opening"), the content is written in place, and a      *)
(* failure closes the file, which drops the lock, before deleting the key  *)
(* by its path. A lock refused after the claim leaves the claimed file,    *)
(* and the store, finding the key's file there, answers Exists.            *)
(* Protocol "claimfix" is the same, with the failed put's file deleted     *)
(* while its lock still keeps the others out.                              *)
(* Protocol "whole" writes a temporary file that no other process names,   *)
(* then publishes it in one system call refused when the key names a file: *)
(* an exclusive rename (renamex_np, renameat2), or link(2) where the file  *)
(* system has none, or on Windows a move that does not replace.            *)
(* Protocol "move" publishes by File.Move(temp, key, overwrite: false),    *)
(* which .NET runs on Unix as lstat(key) followed by rename(2).            *)
(*                                                                          *)
(* A reader opens the key, then takes a shared flock as a second step, and *)
(* reads what the file it opened holds. Refused the lock, it opens again,  *)
(* up to its patience. The repair reads the key's file the same way and,   *)
(* finding it not whole, deletes the key by its path.                      *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Writers,    \* the puts of the key, each writing a file of its own named by it
    Readers,    \* the handles that read the key: refreshes, rebases, opens
    Protocol,   \* "claim", "claimfix", "whole" or "move"
    Locking,    \* whether flock works on this file system
    Repair,     \* whether RemoveTornCommitAsync runs
    Crashes,    \* whether a writer may die at any step
    Patience    \* how many times a reader opens the key again after a refused lock

ASSUME Protocol \in {"claim", "claimfix", "whole", "move"}
ASSUME Locking \in BOOLEAN /\ Repair \in BOOLEAN /\ Crashes \in BOOLEAN
ASSUME Patience \in Nat

Free == "free"
None == "none"
Repairer == "repair"
Claims == Protocol \in {"claim", "claimfix"}

VARIABLES
    path,           \* the file the key names, or Free
    content,        \* [Writers -> {"absent", "empty", "zeros", "whole"}]
    exLock,         \* [Writers -> BOOLEAN]: the writer's exclusive lock on its file
    shLocks,        \* [Writers -> SUBSET (Readers \cup {Repairer})]: shared locks on each file
    wpc,            \* each writer's step
    answer,         \* [Writers -> {"none", "created", "exists", "failed"}]
    heldAtExists,   \* the file the key named when a writer answered "exists"
    rpc, rnode, rtries, result,
    xpc, xnode, xtries

wvars == <<content, exLock, wpc, answer, heldAtExists>>
rvars == <<rpc, rnode, rtries, result>>
xvars == <<xpc, xnode, xtries>>
vars == <<path, shLocks, wvars, rvars, xvars>>

Init == /\ path = Free
        /\ content = [w \in Writers |-> "absent"]
        /\ exLock = [w \in Writers |-> FALSE]
        /\ shLocks = [w \in Writers |-> {}]
        /\ wpc = [w \in Writers |-> "start"]
        /\ answer = [w \in Writers |-> "none"]
        /\ heldAtExists = [w \in Writers |-> Free]
        /\ rpc = [r \in Readers |-> "open"]
        /\ rnode = [r \in Readers |-> Free]
        /\ rtries = [r \in Readers |-> 0]
        /\ result = [r \in Readers |-> "none"]
        /\ xpc = IF Repair THEN "list" ELSE "done"
        /\ xnode = Free
        /\ xtries = 0

SetPc(w, s) == wpc' = [wpc EXCEPT ![w] = s]

Answer(w, a) == /\ answer' = [answer EXCEPT ![w] = a]
                /\ wpc' = [wpc EXCEPT ![w] = "done"]

AnswerExists(w) == /\ Answer(w, "exists")
                   /\ heldAtExists' = [heldAtExists EXCEPT ![w] = path]

(* ---------------------------- the writers ---------------------------- *)

\* claim: open(2) with O_CREAT|O_EXCL claims the key (FileObjectStore.cs:394-401). A taken key
\* throws, and the store answers Exists (:402-405).
ClaimOpen(w) ==
    /\ Claims /\ wpc[w] = "start"
    /\ IF path # Free
       THEN /\ AnswerExists(w)
            /\ UNCHANGED <<path, content>>
       ELSE /\ path' = w
            /\ content' = [content EXCEPT ![w] = "empty"]
            /\ SetPc(w, IF Locking THEN "lock" ELSE "write")
            /\ UNCHANGED <<answer, heldAtExists>>
    /\ UNCHANGED <<exLock, shLocks, rvars, xvars>>

\* claim: the flock .NET takes after open (SafeFileHandle.Unix.cs, Init). Refused, it throws and
\* leaves the file it created, and the store, finding the key's file, answers Exists (:402-405).
\* claimfix tells the refused lock from a taken key, deletes the file it created, and fails.
ClaimLock(w) ==
    /\ Claims /\ wpc[w] = "lock"
    /\ IF shLocks[w] = {}
       THEN /\ exLock' = [exLock EXCEPT ![w] = TRUE]
            /\ SetPc(w, "write")
            /\ UNCHANGED <<path, answer, heldAtExists>>
       ELSE /\ IF Protocol = "claimfix"
               THEN /\ path' = Free
                    /\ Answer(w, "failed")
                    /\ UNCHANGED heldAtExists
               ELSE /\ IF path # Free
                       THEN AnswerExists(w)
                       ELSE Answer(w, "failed") /\ UNCHANGED heldAtExists
                    /\ UNCHANGED path
            /\ UNCHANGED exLock
    /\ UNCHANGED <<content, shLocks, rvars, xvars>>

\* whole and move: a temporary file, named by no key and by no other process.
TempOpen(w) ==
    /\ ~Claims /\ wpc[w] = "start"
    /\ content' = [content EXCEPT ![w] = "empty"]
    /\ SetPc(w, "write")
    /\ UNCHANGED <<path, exLock, shLocks, answer, heldAtExists, rvars, xvars>>

\* RandomAccess.SetLength (FileObjectStore.cs:421): the file has its length, in zeros.
Size(w) ==
    /\ wpc[w] = "write"
    /\ content' = [content EXCEPT ![w] = "zeros"]
    /\ SetPc(w, "writing")
    /\ UNCHANGED <<path, exLock, shLocks, answer, heldAtExists, rvars, xvars>>

\* The last byte written.
Finish(w) ==
    /\ wpc[w] = "writing"
    /\ content' = [content EXCEPT ![w] = "whole"]
    /\ SetPc(w, IF Claims THEN "release" ELSE "publish")
    /\ UNCHANGED <<path, exLock, shLocks, answer, heldAtExists, rvars, xvars>>

\* A write fails: a cancellation, a full disk, content shorter than announced.
Fail(w) ==
    /\ wpc[w] = "writing"
    /\ SetPc(w, CASE Protocol = "claim" -> "unlock"
                  [] Protocol = "claimfix" -> "cleanup"
                  [] OTHER -> "discard")
    /\ UNCHANGED <<path, content, exLock, shLocks, answer, heldAtExists, rvars, xvars>>

\* claim, success: leaving `using (handle)` closes the file, which drops the lock.
Release(w) ==
    /\ wpc[w] = "release"
    /\ exLock' = [exLock EXCEPT ![w] = FALSE]
    /\ Answer(w, "created")
    /\ UNCHANGED <<path, content, shLocks, heldAtExists, rvars, xvars>>

\* claim, failure: the `using` closes the file first, which drops the lock (FileObjectStore.cs:417)...
Unlock(w) ==
    /\ wpc[w] = "unlock"
    /\ exLock' = [exLock EXCEPT ![w] = FALSE]
    /\ SetPc(w, "cleanup")
    /\ UNCHANGED <<path, content, shLocks, answer, heldAtExists, rvars, xvars>>

\* ...then the catch deletes whatever file the key names by then (TryDelete(target), :510-515).
\* claimfix deletes while its lock still keeps readers and the repair out, then closes.
Cleanup(w) ==
    /\ wpc[w] = "cleanup"
    /\ path' = Free
    /\ exLock' = [exLock EXCEPT ![w] = FALSE]
    /\ Answer(w, "failed")
    /\ UNCHANGED <<content, shLocks, heldAtExists, rvars, xvars>>

\* whole and move, failure: the temporary file goes, and only this writer named it.
Discard(w) ==
    /\ wpc[w] = "discard"
    /\ Answer(w, "failed")
    /\ UNCHANGED <<path, content, exLock, shLocks, heldAtExists, rvars, xvars>>

\* An exclusive rename, or link(2): one system call, refused with EEXIST when the key names a file
\* (FileObjectStore.Publish).
Show(w) ==
    /\ Protocol = "whole" /\ wpc[w] = "publish"
    /\ IF path = Free
       THEN /\ path' = w
            /\ Answer(w, "created")
            /\ UNCHANGED heldAtExists
       ELSE /\ AnswerExists(w)
            /\ UNCHANGED path
    /\ UNCHANGED <<content, exLock, shLocks, rvars, xvars>>

\* File.Move(temp, key, overwrite: false) on Unix: lstat(key) first...
MoveCheck(w) ==
    /\ Protocol = "move" /\ wpc[w] = "publish"
    /\ IF path = Free
       THEN SetPc(w, "rename") /\ UNCHANGED <<answer, heldAtExists>>
       ELSE AnswerExists(w)
    /\ UNCHANGED <<path, content, exLock, shLocks, rvars, xvars>>

\* ...then rename(2), which replaces whatever file the key names by then.
MoveRename(w) ==
    /\ wpc[w] = "rename"
    /\ path' = w
    /\ Answer(w, "created")
    /\ UNCHANGED <<content, exLock, shLocks, heldAtExists, rvars, xvars>>

\* A process dies: the kernel drops its locks, and its files stay as they are.
Crash(w) ==
    /\ Crashes /\ wpc[w] \notin {"done", "dead"}
    /\ exLock' = [exLock EXCEPT ![w] = FALSE]
    /\ SetPc(w, "dead")
    /\ UNCHANGED <<path, content, shLocks, answer, heldAtExists, rvars, xvars>>

(* ---------------------------- the readers ---------------------------- *)

\* OpenToReadAsync (FileObjectStore.cs:640-664): open(2) of the key...
ROpen(r) ==
    /\ rpc[r] = "open"
    /\ IF path = Free
       THEN /\ result' = [result EXCEPT ![r] = "absent"]
            /\ rpc' = [rpc EXCEPT ![r] = "done"]
            /\ UNCHANGED rnode
       ELSE /\ rnode' = [rnode EXCEPT ![r] = path]
            /\ rpc' = [rpc EXCEPT ![r] = IF Locking THEN "lock" ELSE "read"]
            /\ UNCHANGED result
    /\ UNCHANGED <<path, shLocks, rtries, wvars, xvars>>

\* ...then the shared flock. Refused while a put holds its file, the read pauses and opens again,
\* until its patience runs out.
RLock(r) ==
    /\ rpc[r] = "lock"
    /\ IF ~exLock[rnode[r]]
       THEN /\ shLocks' = [shLocks EXCEPT ![rnode[r]] = @ \cup {r}]
            /\ rpc' = [rpc EXCEPT ![r] = "read"]
            /\ UNCHANGED <<rtries, result>>
       ELSE /\ UNCHANGED shLocks
            /\ IF rtries[r] < Patience
               THEN /\ rtries' = [rtries EXCEPT ![r] = @ + 1]
                    /\ rpc' = [rpc EXCEPT ![r] = "open"]
                    /\ UNCHANGED result
               ELSE /\ result' = [result EXCEPT ![r] = "busy"]
                    /\ rpc' = [rpc EXCEPT ![r] = "done"]
                    /\ UNCHANGED rtries
    /\ UNCHANGED <<path, rnode, wvars, xvars>>

\* The read of the file the reader opened, whatever the key names by now.
RRead(r) ==
    /\ rpc[r] = "read"
    /\ result' = [result EXCEPT ![r] = IF content[rnode[r]] = "whole" THEN "whole" ELSE "torn"]
    /\ shLocks' = [shLocks EXCEPT ![rnode[r]] = @ \ {r}]
    /\ rpc' = [rpc EXCEPT ![r] = "done"]
    /\ UNCHANGED <<path, rnode, rtries, wvars, xvars>>

(* ---------------------------- the repair ---------------------------- *)

\* RemoveTornCommitAsync (VortexDataset.cs:606-616): the newest commit, listed...
XList ==
    /\ xpc = "list"
    /\ xpc' = IF path = Free THEN "done" ELSE "open"
    /\ UNCHANGED <<path, shLocks, xnode, xtries, wvars, rvars>>

\* ...read whole (IsWholeAsync), opened and locked as a reader does...
XOpen ==
    /\ xpc = "open"
    /\ IF path = Free
       THEN xpc' = "done" /\ UNCHANGED xnode
       ELSE /\ xnode' = path
            /\ xpc' = IF Locking THEN "lock" ELSE "read"
    /\ UNCHANGED <<path, shLocks, xtries, wvars, rvars>>

XLock ==
    /\ xpc = "lock"
    /\ IF ~exLock[xnode]
       THEN /\ shLocks' = [shLocks EXCEPT ![xnode] = @ \cup {Repairer}]
            /\ xpc' = "read"
            /\ UNCHANGED xtries
       ELSE /\ UNCHANGED shLocks
            /\ IF xtries < Patience
               THEN xtries' = xtries + 1 /\ xpc' = "open"
               ELSE xpc' = "done" /\ UNCHANGED xtries
    /\ UNCHANGED <<path, xnode, wvars, rvars>>

XRead ==
    /\ xpc = "read"
    /\ shLocks' = [shLocks EXCEPT ![xnode] = @ \ {Repairer}]
    /\ xpc' = IF content[xnode] = "whole" THEN "done" ELSE "delete"
    /\ UNCHANGED <<path, xnode, xtries, wvars, rvars>>

\* ...and, found not whole, deleted by its key (:615), whatever file the key names by then.
XDelete ==
    /\ xpc = "delete"
    /\ path' = Free
    /\ xpc' = "done"
    /\ UNCHANGED <<shLocks, xnode, xtries, wvars, rvars>>

Next ==
    \/ \E w \in Writers :
          \/ ClaimOpen(w) \/ ClaimLock(w) \/ TempOpen(w) \/ Size(w) \/ Finish(w) \/ Fail(w)
          \/ Release(w) \/ Unlock(w) \/ Cleanup(w) \/ Discard(w) \/ Show(w)
          \/ MoveCheck(w) \/ MoveRename(w) \/ Crash(w)
    \/ \E r \in Readers : ROpen(r) \/ RLock(r) \/ RRead(r)
    \/ XList \/ XOpen \/ XLock \/ XRead \/ XDelete

Spec == Init /\ [][Next]_vars

(* ---------------------------- the properties ---------------------------- *)

\* A put that answered Created leaves its whole object under the key. Nothing in this model
\* removes an object on purpose: vacuum never takes the newest commit.
CreatedStays == \A w \in Writers : answer[w] = "created" => path = w /\ content[w] = "whole"

\* A put answers Exists only when another put's file was under the key.
ExistsMeansAnother == \A w \in Writers : answer[w] = "exists" => heldAtExists[w] \notin {Free, w}

\* "A commit being written is never taken for a torn one" (VortexDataset.cs:601-603): a read that
\* finds a file not whole finds one whose writer died or gave up, and never one it then completes.
TornOnlyWhenAbandoned ==
    \A r \in Readers :
        result[r] = "torn" => wpc[rnode[r]] \in {"dead", "done"} /\ answer[rnode[r]] # "created"

\* The key, seen through the store contract: an object appears whole, once, and never leaves.
Slot == IF path # Free /\ content[path] = "whole" THEN path ELSE None
Store == INSTANCE AtomicPut WITH Keys <- {"key"}, Puts <- Writers, Removable <- FALSE,
                                 slots <- [k \in {"key"} |-> Slot]
StoreSafety == Store!SafetySpec

\* Witness: a reader opens the key between a writer's open and its lock.
NoLockGap == ~\E r \in Readers, w \in Writers :
                 rpc[r] \in {"lock", "read"} /\ rnode[r] = w /\ wpc[w] = "lock"

\* Witness: two puts, both written whole, race to publish into the free key.
NoRacingPublish == ~(path = Free /\ \A w \in Writers : wpc[w] = "publish")
=============================================================================
