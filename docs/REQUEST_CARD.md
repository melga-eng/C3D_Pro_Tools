# Request card

Copy this, fill it in, and give it to the agent. Short answers are fine. The two parts that matter most are **Result I
expect** and **Done when**: with those, the agent can check its own work instead of asking you to test.

```
TOOL / TASK:        (new tool, or the name of the existing tool to fix)

GOAL:               (one sentence: what this saves me from doing by hand)

DRAWING:            (path of a drawing to test on; the agent works on a copy)
EXAMPLE OBJECTS:    (network, alignment, sheet or layout names to try it on)

BY HAND TODAY:      (the steps I do now, in order)

RESULT I EXPECT:    (a finished example: a sheet, a screenshot, a PDF, or a drawing where I already did it by hand)

RULES:              (the numbers: cover, clearance, slope, lengths, scales, style names)

HOW I WANT TO RUN IT: (select first? whole network? list of sheets? a preset I reuse?)

NOT NEEDED:         (what the agent must leave out)

DONE WHEN:          (what I will look at to accept it, for example "tile 124 profile sheet matches the PDF")
```

## For a problem with an existing tool

```
TOOL:               
DRAWING + OBJECTS:  
WHAT I DID:         
WHAT HAPPENED:      (screenshot or the error text)
WHAT I EXPECTED:    
```

## For a one-off job in the drawing (no new tool)

```
DRAWING:            
AREA / OBJECTS:     (a boundary polyline, a network, a list of names)
CHANGE:             
RULES:              
MUST NOT CHANGE:    
```
