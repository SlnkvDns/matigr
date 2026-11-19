const demoTournamentId = '0344e06b-4ea1-582e-bd91-e832719773f2';

const statements: Readonly<Partial<Record<string, string>>> = {
  'acdc6594-8ac1-5f18-bb65-def577a79950':
    'Найдите все двузначные натуральные числа, которые при делении на 5 дают остаток 2, а при делении на 7 — остаток 4. Обоснуйте, что других чисел нет.',
  '11bf5e9e-d65b-5d67-b25f-ada9c2cfbcff':
    'Из чисел от 1 до 20 выбрали 11 различных. Докажите, что среди выбранных найдутся два числа, одно из которых делится на другое.',
};

export function demoTaskStatement(tournamentId: string, taskId: string) {
  return tournamentId === demoTournamentId && Object.hasOwn(statements, taskId) ? statements[taskId] : undefined;
}
