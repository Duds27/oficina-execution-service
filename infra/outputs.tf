output "dynamodb_table_name" {
  description = "Nome da tabela (variável DynamoDb__TableName do pod)."
  value       = aws_dynamodb_table.execucoes.name
}

output "dynamodb_table_arn" {
  value = aws_dynamodb_table.execucoes.arn
}

output "pod_role_arn" {
  description = "ARN da role do pod, entregue à ServiceAccount pelo Pod Identity (sem anotação)."
  value       = aws_iam_role.execution_service.arn
}

output "ecr_repository_url" {
  description = "Repositório da imagem do serviço."
  value       = aws_ecr_repository.execution_service.repository_url
}
