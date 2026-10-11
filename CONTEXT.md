# Font Maintenance Context

This context describes how ASS Subsetter inventories, compares, retains, renames, and quarantines fonts used for subtitle processing.

## Language

**Font File**:
A physical TTF, OTF, TTC, or OTC file discovered in a configured font directory. A Font File may contain one or more Font Faces.
_Avoid_: Font when the physical container matters

**Font Face**:
One independently selectable face within a Font File, identified by its Face Index. Duplicate relationships are always classified between two Font Faces.
_Avoid_: Font entry, style file

**Font Relationship**:
The single highest-priority classification that describes how two Font Faces relate after direct comparison.
_Avoid_: Similarity score, duplicate level

**Direct Replacement**:
A proven, directional relationship in which one Font Face preserves every required behavior of another. Direct Replacement is never inferred transitively.
_Avoid_: Probably equivalent, indirect replacement

**Duplicate Group**:
A set of Font Faces for which a retained candidate has been directly verified against every candidate it may replace. Graph connectivity alone never creates a Duplicate Group.
_Avoid_: Connected component, duplicate cluster

**Retained Item**:
A Font File selected to remain available after hard safety constraints and within-group retention scoring have been applied.
_Avoid_: Winner, master font

**Protected Directory**:
A directory whose Font Files may be inventoried and reported but must never be renamed, quarantined, or deleted.
_Avoid_: Read-only directory when protection is a policy rather than a filesystem permission

**Quarantine**:
A recoverable state in which a redundant Font File has been transactionally moved out of its original directory while its evidence and original path remain recorded.
_Avoid_: Delete, trash

**Quarantine Batch**:
An immutable record of one requested quarantine operation and the per-file outcome of that operation.
_Avoid_: Cleanup run

**Unclassifiable Face**:
A Font Face whose content cannot be completely parsed or verified and therefore cannot receive a Font Relationship or participate in automatic quarantine.
_Avoid_: Independent font, broken duplicate

**Rename Plan**:
An immutable proposal mapping eligible Font Files from their current names to deterministic normalized names. Producing a Rename Plan never changes a file.
_Avoid_: Rename operation
