terraform {
  required_version = ">= 1.5.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.60"
    }
  }

  # Estado remoto compartilhado (mesmo bucket S3 + trava DynamoDB da Fase 3).
  # O pipeline completa com: terraform init -backend-config="bucket=..." \
  #   -backend-config="key=execution-service/<ambiente>/terraform.tfstate" \
  #   -backend-config="region=..." -backend-config="dynamodb_table=..."
  backend "s3" {
    key = "execution-service/terraform.tfstate"
  }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = local.tags
  }
}
