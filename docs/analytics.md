# Analytics definitions

Flow is a daily count of work items in each workflow category, taken from the daily
analytics snapshot. Lead time is elapsed time from item creation to its latest completed
transition. Cycle time starts at the last transition into `Active` before completion; a
reopened item therefore measures its most recent active interval. Percentiles use nearest
rank (the smallest value at or above the requested percentile).
