Rework the current input system to support doubleclick and better click and hold behaviour.

Features wanted among others:
1. Double clicking on same object should possibly invoke a different behaviour.
2. "Double clicking" where clicks are on different objects is not double clicking, just two separate clicks.
3. Click and hold on an object and then release on that object should single click.
4. Click and hold on an object, then moving mouse outside of it and then release, should not click at all.
5. Click and hold outside of object, then move mouse inside object and then release, should not click either.
6. Above rules are for ui objects, for world objects fire eagerly on mouse down so we don't delay time dependent clicks such as clicking on a unit etc.

Core implementation bits:
Make a persistent input class to govern this.
Add functions to that input class to handle double click logic etc, such as when to invalidate it. For now just follow above rules. Add setting for how long double click time window should be. Start it at like 500ms.

Then start by migrating one system to this. Main menu seems like a good start.

As for double clicking, I wanna doble click references in the widget editor to select the object the reference points to. That would be a simple task alone, but I want this to be solved globally.
