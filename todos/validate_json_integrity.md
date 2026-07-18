We need to validate json etc data we read is well formed and so on.

Currently we parse strings into enums at runtime which is extremely irresponsible, we should do that when we load the data and parse them into actual useful information, and print errors when things fails.

I just had an issue where I wrote a value in a spell, and it turns out that value didn't map to any of the enums so was silently dropped for the default value. That is very bad usability.

So there are two parts here:
1. Ensure we parse and validate all such enum strings in the json as we read the data, and print errors for whatever fails.
2. Move away from using these directly json mapped data structures in the hot game loops, parse strings down to enums etc and use those, that way its impossible to fail parsing at runtime, so we only get these errors on startup.
