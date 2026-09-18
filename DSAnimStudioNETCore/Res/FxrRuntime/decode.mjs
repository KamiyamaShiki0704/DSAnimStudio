// One isolated decode per invocation; original game resources arrive on stdin.
import { FXR, FXRSerializeFilter, Game } from './fxr.mjs';
const games = { ER: Game.EldenRing, NR: Game.Nightreign, DS3: Game.DarkSouls3, SDT: Game.Sekiro, AC6: Game.ArmoredCore6 };
try {
  const game = games[process.argv[2]];
  if (game === undefined) throw new Error('Unsupported game');
  const chunks = []; let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    if (size > 32 * 1024 * 1024) throw new Error('FXR input budget exceeded');
    chunks.push(chunk);
  }
  const bytes = Buffer.concat(chunks);
  const input = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  const decoded = FXR.read(input, game);
  // Old cylinder actions have only direction / emitInside. The native getter
  // returns false for the absent third field; the authoring library defaults
  // new cylinders to true. Preserve this distinction before serialization.
  const nodes = [...decoded.root.walk()];
  if (nodes.some(n => n.configs?.some(c => c.emitterShape?.type === 405))) {
    const rawNodes = [...FXR.read(input, Game.Generic).root.walk()];
    for (let i = 0; i < nodes.length; ++i)
      for (let j = 0; j < (nodes[i].configs?.length ?? 0); ++j) {
        const shape = nodes[i].configs[j].emitterShape;
        if (shape?.type !== 405) continue;
        const raw = rawNodes[i].configs[j].actions.find(a => a?.type === 405);
        if (!raw || raw.fields1.length < 3) shape.yAxis = false;
      }
  }
  const result = decoded.serialize({ filter: FXRSerializeFilter.DefaultUnknowns });
  const data = result.fxr ?? result;
  if (!data.root) throw new Error('Missing FXR root');
  const states = decoded.states.map(s => s.conditions.map(c => ({
    operator: c.operator, unk1: c.unk1, state: c.state,
    leftOperandType: c.leftOperandType, leftOperandValue: c.leftOperandValue,
    rightOperandType: c.rightOperandType, rightOperandValue: c.rightOperandValue
  })));
  process.stdout.write(JSON.stringify({ Id: data.id, Parser: '@cccode/fxr 32.1.0', Root: data.root, States: states }));
} catch (error) {
  // No local installation paths or full source dumps in user-facing diagnostics.
  process.stderr.write(String(error.message).slice(0, 240)); process.exitCode = 1;
}
