namespace Agentweaver.Squad.Naming;

/// <summary>
/// Static catalog of universe to ordered name pools used for casting member names.
/// </summary>
public static class UniversePools
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Pools =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["The Matrix"] = new[] { "Neo", "Trinity", "Morpheus", "Tank", "Link", "Dozer", "Switch", "Apoc", "Mouse", "Seraph", "Oracle", "Niobe", "Ghost" },
            ["Star Wars"] = new[] { "Luke", "Leia", "Han", "Chewie", "Obi-Wan", "Yoda", "Vader", "Ahsoka", "Rex", "Cody", "Mace", "Padme", "Qui-Gon" },
            ["Inception"] = new[] { "Cobb", "Ariadne", "Arthur", "Eames", "Yusuf", "Saito", "Fischer", "Mal", "Miles" },
            ["Firefly"] = new[] { "Mal", "Zoe", "Wash", "Inara", "Jayne", "Kaylee", "Simon", "River", "Book", "Shepherd", "Niska", "Saffron" },
            ["The Office"] = new[] { "Michael", "Dwight", "Jim", "Pam", "Ryan", "Kelly", "Andy", "Phyllis", "Stanley", "Kevin", "Oscar", "Toby", "Angela", "Darryl", "Creed", "Meredith" },
            ["Breaking Bad"] = new[] { "Walt", "Jesse", "Skyler", "Hank", "Mike", "Saul", "Gus", "Kim", "Nacho", "Lydia", "Badger", "Skinny Pete" },
            ["Dune"] = new[] { "Paul", "Jessica", "Duncan", "Gurney", "Stilgar", "Chani", "Alia", "Leto", "Vladimir", "Piter", "Feyd", "Thufir" },
            ["Alien"] = new[] { "Ripley", "Dallas", "Ash", "Parker", "Lambert", "Hudson", "Hicks", "Bishop", "Vasquez", "Drake", "Ferro", "Apone", "Newt" },
            ["Blade Runner"] = new[] { "Deckard", "Roy", "Rachael", "Pris", "Zhora", "Batty", "Bryant", "Holden", "Gaff", "Tyrell" },
            ["The Lord of the Rings"] = new[] { "Frodo", "Sam", "Gandalf", "Aragorn", "Legolas", "Gimli", "Boromir", "Merry", "Pippin", "Galadriel", "Elrond", "Bilbo", "Faramir" },
            ["Star Trek"] = new[] { "Kirk", "Spock", "McCoy", "Scotty", "Uhura", "Sulu", "Chekov", "Picard", "Riker", "Data", "Worf", "Crusher", "Troi" },
            ["Harry Potter"] = new[] { "Harry", "Hermione", "Ron", "Dumbledore", "Hagrid", "Snape", "McGonagall", "Sirius", "Luna", "Neville", "Ginny", "Dobby" },
            ["The Avengers"] = new[] { "Stark", "Rogers", "Romanoff", "Banner", "Thor", "Barton", "Fury", "Wanda", "Vision", "Parker", "Strange", "Rhodes" },
            ["Battlestar Galactica"] = new[] { "Adama", "Roslin", "Apollo", "Starbuck", "Tigh", "Baltar", "Boomer", "Helo", "Athena", "Chief", "Gaeta", "Dualla" },
        };
}
