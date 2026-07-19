### How to read this doc
*anti patterns to avoid and principles to follow*

*Egregious anti patterns should typically be refactored whenever found even if not asked to by the user, always, tell the main claude about these when found, and log them in [anti-patterns-list.md].*
*Regular anti patterns should be documented in [anti-patterns-list.md] whenever found, and if its relevant to the caller claudes request bring these up as potential refactors or fixes as he goes.*
*All anti pattern potential that the caller claude could be thought to do as it tries to solve the problem asked for should be brought up and explain what it should try to do instead in this case.*

# Principle: Single Source of Truth
Every distinct behavior or pattern should have one canonical implementation. Before writing new code, check whether an existing system, utility, or pattern already solves the problem. The goal is fewer pieces of code doing the same function — when a bug is fixed, it's fixed in one place.

## Anti Pattern: Two functions being used for the same thing so needs to be kept in sync or its a bug.
These are fine sometimes when its done close to each other to make an implementation simpler, its never fine when its done in separate files.

## **Egregious Anti Pattern**: Rendering and click handling being done on different implementations
This one is a very egregious and common anti pattern so needs special mention. Never ever tolerate the click handling functions to define their own areas to look for clicks separate from what the draw calls work on.

Example pseudocode:
`def Render(): DrawButton(rect(10, 10, 180, 30))`
`def Update(): if ClickInRect(16, 10, 180, 30): DoSomething()`
This is very bad since it still works, but it feels very bad since parts of the button wont be clickable. These sort of UI issues are so egregiously bad that we should never accept this sort of behaviour.

Always generate a list of positions that then gets reused for both drawing and click handling.

# Principle: Animations should never affect gameplay
The entire game should be playable in theory with no sprites or animations. any behaviour that depends on animation timings etc should be fixed, so the behaviour runs on separate timings outside of animation code.

## **Anti Pattern**: Waiting for animation to be done
This makes us dependent on the animation timings, refactor this to put the timings in the gameplay code instead and adjust animation speed to match the actual gameplay data.

## **Egregious Anti Pattern**: Putting gameplay function calls in animation code
This is especially bad since its very hard to track what the gameplay code actually does when you do this. Since now when you set an animation you not only set the visuals you are also declaring a gameplay function.
