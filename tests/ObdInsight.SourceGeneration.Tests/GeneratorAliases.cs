extern alias Generator;

// The generator embeds its annotation sources. Runtime compilation tests also
// reference Core/Annotations, so keep the compiler assembly's duplicates private.
global using CanSignalGenerator = Generator::ObdInsight.SourceGeneration.CanSignalGenerator;
global using UdsGenerator = Generator::ObdInsight.SourceGeneration.UdsGenerator;
