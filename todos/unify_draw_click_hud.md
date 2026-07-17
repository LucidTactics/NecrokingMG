In HudLayers.cs a lot of the layers just handle click or draw, not both.

This creates a lot of bug prone moments where a layers click is active but not its visuals, and vice versa.
Also the draw order might not align with click order this way.

Make sure that the drawing and clicking is the same in all of these layers. Split drawing functions if its not trivial to do.
