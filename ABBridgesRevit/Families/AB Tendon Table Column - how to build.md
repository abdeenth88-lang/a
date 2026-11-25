# AB Tendon Table Column.rfa (2D, Detail Item)

One column of the tendon table (DISTANCE ALONG C/L / DISTANCE FROM TOP OF BOX). AB Bridges places one instance per
point, side by side, and fills the parameters. Put the file here: `<add-in folder>\Families\AB Tendon Table Column.rfa`.
If the file is missing, AB Bridges draws the table with lines + text (no change needed).

1. New family -> template **Metric Detail Item.rft**.
2. Reference planes: Left (on the origin), Right, Top (on the origin), Middle, Bottom. Vertical Center.
3. Dimensions + parameters (Length):
   - Left -> Right = **Width** (instance), Center equal (EQ) between Left and Right.
   - Top -> Middle = **Row 1 H** (type), Middle -> Bottom = **Row 2 H** (type).
4. Detail lines (thin), locked to the planes: top, middle, bottom, right edge (the left edge is the previous column's right).
   Optional: left line with Visible = **Left Line** (Yes/No, instance).
5. Two **Labels** (Arial 2.0 mm, rotated 90°, centered), each locked to the Center plane:
   - in row 1: parameter **X Label** (Text, instance), centered between Top and Middle.
   - in row 2: parameter **Y Value** (Text, instance), centered between Middle and Bottom.
6. Defaults: Width 8 mm, Row 1 H 14 mm, Row 2 H 14 mm. Save as `AB Tendon Table Column.rfa`.

Insertion point = top-left corner (Left x Top). AB Bridges sets Row 1 H / Row 2 H to fit the longest text.
