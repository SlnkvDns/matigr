import { demoTournamentId } from '../../shared/tournament';

const tasks: Readonly<Partial<Record<string, { statement: string; solution: string }>>> = {
  'acdc6594-8ac1-5f18-bb65-def577a79950': {
    statement: 'Найдите все двузначные натуральные числа, которые при делении на 5 дают остаток 2, а при делении на 7 — остаток 4. Обоснуйте, что других чисел нет.',
    solution: 'Искомое число имеет вид 5k + 2. Условие про деление на 7 даёт 5k ≡ 2 (по модулю 7), откуда k ≡ 6 (по модулю 7). Значит, число имеет вид 32 + 35m. Из таких чисел двузначны только 32 и 67.',
  },
  '11bf5e9e-d65b-5d67-b25f-ada9c2cfbcff': {
    statement: 'Из чисел от 1 до 20 выбрали 11 различных. Докажите, что среди выбранных найдутся два числа, одно из которых делится на другое.',
    solution: 'Разделим каждое из выбранных чисел на 2 столько раз, сколько возможно. Получится одно из десяти нечётных чисел от 1 до 19. Выбрано 11 чисел, поэтому у двух из них получилась одинаковая нечётная часть. Тогда большее из этих двух чисел получается из меньшего умножением на степень двойки и делится на него.',
  },
};

const legacyMaterial = /^Подготовленное принятое решение\.\r?\nКоманда: (?:Альфа|Бета|Гамма)\r?\nЗадача [12]\r?\nМатериал тестового профиля Matigr v1; доступен только назначенному жюри\.$/u;

export function demoTask(tournamentId: string, taskId: string) {
  return tournamentId === demoTournamentId && Object.hasOwn(tasks, taskId) ? tasks[taskId] : undefined;
}

export function isDemoMaterialPlaceholder(text: string) {
  return legacyMaterial.test(text.trim());
}
